#!/usr/bin/env python3
"""
Protocol probe for the QwenTtsCloud worker shim.

Spawns the replacement QwenTtsWorker.exe exactly the way the game does, speaks the
binary stdin/stdout protocol, and writes the returned audio to a WAV file so it can
be listened to.

Usage:
    python tools/probe.py --exe path/to/QwenTtsWorker.exe \
        --ref "C:/.../kolkata-voices/audio.wav" \
        --text "Привіт, це тест синтезу." --lang Russian \
        --provider mock --out out.wav --repeat 2

Requires ffmpeg on PATH (used to decode the reference clip to 24 kHz mono float).
"""

from __future__ import annotations

import argparse
import json
import os
import struct
import subprocess
import sys
import tempfile
import threading
import time
import wave

MAX_SAMPLES = 720000


def encode_string(value: str) -> bytes:
    """System.IO.BinaryWriter.Write(string): 7-bit length prefix + UTF-8 bytes."""
    data = value.encode("utf-8")
    length = len(data)
    out = bytearray()
    while length >= 0x80:
        out.append((length & 0x7F) | 0x80)
        length >>= 7
    out.append(length)
    return bytes(out) + data


def load_reference(path: str, seconds: float = 3.0) -> list[float]:
    """Decodes any audio file to 24 kHz mono float samples using ffmpeg."""
    cmd = [
        "ffmpeg", "-v", "error", "-i", path,
        "-ac", "1", "-ar", "24000", "-f", "f32le", "-acodec", "pcm_f32le", "-",
    ]
    raw = subprocess.run(cmd, check=True, capture_output=True).stdout
    samples = list(struct.unpack(f"<{len(raw) // 4}f", raw))
    limit = int(24000 * seconds)
    samples = samples[: min(limit, 240000)]
    if len(samples) < 2400:
        raise SystemExit(f"reference is too short ({len(samples)} samples), need >= 2400")
    samples = [max(-1.0, min(1.0, s)) for s in samples]
    return samples


class Worker:
    def __init__(self, exe: str, cwd: str | None = None, verbose: bool = True):
        self.proc = subprocess.Popen(
            [exe],
            cwd=cwd or os.path.dirname(os.path.abspath(exe)),
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            bufsize=0,
        )
        self.verbose = verbose
        self.total_read = 0
        self.last_raw = b""
        self.stderr_lines: list[str] = []
        self._reader = threading.Thread(target=self._drain_stderr, daemon=True)
        self._reader.start()

    def _drain_stderr(self) -> None:
        assert self.proc.stderr is not None
        for line in iter(self.proc.stderr.readline, b""):
            text = line.decode("utf-8", "replace").rstrip()
            self.stderr_lines.append(text)
            if self.verbose:
                print(f"  [worker] {text}")

    def send(self, payload: bytes) -> None:
        assert self.proc.stdin is not None
        self.proc.stdin.write(payload)
        self.proc.stdin.flush()

    def read_exact(self, count: int) -> bytes:
        assert self.proc.stdout is not None
        data = b""
        while len(data) < count:
            chunk = self.proc.stdout.read(count - len(data))
            if not chunk:
                raise EOFError(f"worker closed stdout after {len(data)}/{count} bytes")
            data += chunk
        self.total_read += len(data)
        return data

    def read_int(self) -> int:
        raw = self.read_exact(4)
        self.last_raw = raw
        return struct.unpack("<i", raw)[0]

    def read_string(self) -> str:
        length = 0
        shift = 0
        while True:
            byte = self.read_exact(1)[0]
            length |= (byte & 0x7F) << shift
            if byte < 0x80:
                break
            shift += 7
        return self.read_exact(length).decode("utf-8")

    def load(self, talker: str, codec: str, backend: str = "CPU") -> None:
        started = time.monotonic()
        self.send(b"\x01\x00\x00\x00" + encode_string(talker) +
                  encode_string(codec) + encode_string(backend))
        code = self.read_int()
        if code == 4:
            raise SystemExit("load failed: " + self.read_string())
        if code != 1 or self.read_int() != 1:
            raise SystemExit(f"unexpected load reply {code}")
        print(f"load ok in {time.monotonic() - started:.2f}s (read {self.total_read} bytes)")

    def synthesize(self, text: str, language: str, reference: list[float]) -> tuple[list[float], float, float]:
        started = time.monotonic()
        payload = bytearray(b"\x02\x00\x00\x00")
        payload += encode_string(text)
        payload += encode_string(language)
        payload += struct.pack("<i", len(reference))
        payload += struct.pack(f"<{len(reference)}f", *reference)
        self.send(bytes(payload))

        pcm: list[float] = []
        first_audio = None
        while True:
            code = self.read_int()
            if code == 3:
                break
            if code == 4:
                raise SystemExit("synthesis failed: " + self.read_string())
            if code != 2:
                raise SystemExit(f"unexpected reply {code} (raw {self.last_raw.hex(' ')})")
            count = self.read_int()
            if count <= 0 or count > MAX_SAMPLES or len(pcm) + count > MAX_SAMPLES:
                raise SystemExit(f"invalid chunk size {count}")
            raw = self.read_exact(count * 4)
            if first_audio is None:
                first_audio = time.monotonic()
            pcm.extend(struct.unpack(f"<{count}f", raw))

        finished = time.monotonic()
        if first_audio is None:
            first_audio = finished
        return pcm, first_audio - started, finished - started

    def close(self) -> int:
        try:
            assert self.proc.stdin is not None
            self.proc.stdin.close()
        except OSError:
            pass
        try:
            return self.proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.proc.kill()
            return -1


def save_wav(path: str, samples: list[float]) -> None:
    with wave.open(path, "wb") as out:
        out.setnchannels(1)
        out.setsampwidth(2)
        out.setframerate(24000)
        out.writeframes(struct.pack(f"<{len(samples)}h",
                                    *(int(max(-1.0, min(1.0, s)) * 32767) for s in samples)))


def write_config(provider: str, extra: dict | None = None) -> str:
    """Writes a throwaway config and points the worker at it through SWYF_TTS_CONFIG."""
    path = os.path.join(tempfile.gettempdir(), "swyf-probe.config.json")
    os.environ["SWYF_TTS_CONFIG"] = path
    config = {
        "provider": provider,
        "openai": {"apiKey": "", "baseUrl": "https://api.openai.com/v1",
                   "model": "gpt-4o-mini-tts", "voice": None, "instructions": None, "speed": None},
        "elevenlabs": {"apiKey": "", "baseUrl": "https://api.elevenlabs.io/v1",
                       "modelId": "eleven_multilingual_v2", "autoClone": True,
                       "defaultVoiceId": "21m00Tcm4TlvDq8ikWAM",
                       "stability": 0.5, "similarityBoost": 0.8},
        "cache": {"enabled": True, "maxMegabytes": 512},
        "fallback": {"passthrough": False, "originalExe": "QwenTtsWorker.orig.exe"},
        "network": {"timeoutSeconds": 20, "maxAttempts": 2},
        "logging": {"verbose": True},
    }
    if extra:
        for key, value in extra.items():
            if isinstance(value, dict) and isinstance(config.get(key), dict):
                config[key].update(value)
            else:
                config[key] = value
    with open(path, "w", encoding="utf-8") as handle:
        json.dump(config, handle, indent=2)
    return path


def main() -> int:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")
        except (AttributeError, ValueError):
            pass

    parser = argparse.ArgumentParser()
    parser.add_argument("--exe", required=True)
    parser.add_argument("--ref", required=True)
    parser.add_argument("--text", default="Hello there, this is a test of the cloud speech worker.")
    parser.add_argument("--lang", default="English")
    parser.add_argument("--out", default="probe.wav")
    parser.add_argument("--repeat", type=int, default=1, help="send the same request N times (cache test)")
    parser.add_argument("--provider", default=None, help="write this provider into cloud-tts.config.json")
    parser.add_argument("--openai-base-url", default=None, help="override openai.baseUrl (for a local stub)")
    parser.add_argument("--openai-key", default=None, help="override openai.apiKey")
    parser.add_argument("--openai-voice", default=None, help="override openai.voice")
    parser.add_argument("--empty-cache", action="store_true", help="delete the local audio cache first")
    parser.add_argument("--quiet-worker", action="store_true")
    args = parser.parse_args()

    exe = os.path.abspath(args.exe)
    if not os.path.isfile(exe):
        raise SystemExit(f"not found: {exe}")

    if args.provider:
        extra = {}
        openai = {}
        if args.openai_base_url:
            openai["baseUrl"] = args.openai_base_url
        if args.openai_key:
            openai["apiKey"] = args.openai_key
        if args.openai_voice:
            openai["voice"] = args.openai_voice
        if openai:
            extra["openai"] = openai
        print("config:", write_config(args.provider, extra))

    if args.empty_cache:
        cache = os.path.join(os.environ.get("LOCALAPPDATA", ""), "SWYF-CloudTTS", "cache")
        if os.path.isdir(cache):
            for name in os.listdir(cache):
                os.remove(os.path.join(cache, name))
        print("cache cleared:", cache)

    print("reference:", args.ref)
    reference = load_reference(args.ref)
    print(f"reference: {len(reference)} samples ({len(reference) / 24000:.2f}s)")

    worker = Worker(exe, verbose=not args.quiet_worker)
    try:
        worker.load(r"C:\models\talker.gguf", r"C:\models\codec.gguf", "CPU")
        for index in range(1, args.repeat + 1):
            pcm, first, total = worker.synthesize(args.text, args.lang, reference)
            print(f"run {index}: {len(pcm)} samples ({len(pcm) / 24000:.2f}s audio), "
                  f"first audio {first * 1000:.0f} ms, total {total * 1000:.0f} ms")
            if index == 1:
                save_wav(args.out, pcm)
                print("wrote", args.out)
    finally:
        code = worker.close()
        print("worker exit code:", code)

    return 0


if __name__ == "__main__":
    sys.exit(main())
