#!/usr/bin/env python3
"""
Local stand-in for a Qwen3-TTS server (k8bit/qwen3-tts dialect), used to test qwenremote
without a GPU.

Serves:
  GET  /health                       -> {"status":"ok","voices":["kate"]}
  GET  /v1/audio/voices              -> {"voices":["kate","dmitri"]}
  POST /v1/audio/speech              -> 24 kHz mono 16-bit WAV
  POST /v1/audio/speech/clone        -> same, unless --no-clone is passed, then HTTP 400
  POST /tts                          -> same, but only with the right X-API-Key

With --tts-only the server behaves like a custom VoiceDesign wrapper (utils/server.py):
only POST /tts exists, it wants the X-API-Key header and the body
{"text", "voice_prompt", "language"}.

Texts containing "FAIL" return HTTP 500 so retry behaviour can be exercised.
"""

from __future__ import annotations

import json
import struct
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from math import sin, pi

SAMPLE_RATE = 24000
NO_CLONE = False
TTS_ONLY = False
API_KEY = "k7Qp2xW9mZ4v"


def tone(seconds: float, frequency: float = 440.0) -> bytes:
    count = int(SAMPLE_RATE * seconds)
    frames = bytearray()
    for i in range(count):
        envelope = min(1.0, i / 400.0) * min(1.0, (count - i) / 400.0)
        frames += struct.pack("<h", int(7000 * envelope * sin(2 * pi * frequency * i / SAMPLE_RATE)))
    return bytes(frames)


def wav(samples: bytes) -> bytes:
    header = struct.pack(
        "<4sI4s4sIHHIIHH4sI",
        b"RIFF", 36 + len(samples), b"WAVE",
        b"fmt ", 16, 1, 1, SAMPLE_RATE, SAMPLE_RATE * 2, 2, 16,
        b"data", len(samples),
    )
    return header + samples


def _value_start(body: bytes, name: str) -> int | None:
    # Workers before 1.4 (HttpClient) wrote name=text; newer ones quote it like browsers do.
    for marker in (b"name=" + name.encode(), b'name="' + name.encode() + b'"'):
        start = 0
        while True:
            start = body.find(marker, start)
            if start < 0:
                break
            end = start + len(marker)
            following = body[end:end + 1]
            if following in (b'"', b"\r", b";"):
                head_end = body.find(b"\r\n\r\n", end)
                return head_end + 4 if head_end >= 0 else None
            start = end
    return None


def multipart_field(body: bytes, name: str) -> str | None:
    value_start = _value_start(body, name)
    if value_start is None:
        return None
    value_end = body.find(b"\r\n--", value_start)
    if value_end < 0:
        return None
    return body[value_start:value_end].decode("utf-8", "replace")


def multipart_size(body: bytes, name: str) -> int:
    value_start = _value_start(body, name)
    if value_start is None:
        return 0
    value_end = body.find(b"\r\n--", value_start)
    return value_end - value_start if value_end >= 0 else 0


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt: str, *args) -> None:
        sys.stderr.write("fake-qwen: " + (fmt % args) + "\n")

    def reply(self, status: int, payload: bytes, content_type: str) -> None:
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(payload)
        self.close_connection = True

    def do_GET(self) -> None:
        if TTS_ONLY:
            self.reply(404, b'{"detail":"not found"}', "application/json")
            return
        if self.path == "/health":
            self.reply(200, json.dumps({"status": "ok", "voices": ["kate", "dmitri"]}).encode(),
                       "application/json")
            return
        if self.path in ("/v1/audio/voices", "/v1/voices"):
            self.reply(200, json.dumps({"voices": ["kate", "dmitri"]}).encode(),
                       "application/json")
            return
        self.reply(404, b'{"detail":"not found"}', "application/json")

    def do_POST(self) -> None:
        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length)
        auth = self.headers.get("Authorization", "")
        content_type = self.headers.get("Content-Type", "")

        if TTS_ONLY and self.path != "/tts":
            # A VoiceDesign wrapper exposes nothing but /tts.
            self.reply(404, b'{"detail":"not found"}', "application/json")
            return

        if self.path == "/v1/audio/speech/clone":
            text = multipart_field(body, "text") or ""
            language = multipart_field(body, "language") or ""
            reference_bytes = multipart_size(body, "reference_audio")
            if not text:
                print("fake-qwen: clone body head=" + repr(body[:400]), flush=True)
            print(f"fake-qwen: clone auth={auth[:12]} lang={language} "
                  f"reference={reference_bytes} bytes chars={len(text)}", flush=True)
            if NO_CLONE:
                self.reply(400, b'{"detail":"cloning is disabled in CustomVoice mode"}',
                           "application/json")
                return
        elif self.path == "/v1/audio/speech":
            try:
                payload = json.loads(body)
            except json.JSONDecodeError:
                self.reply(400, b'{"detail":"bad json"}', "application/json")
                return
            text = payload.get("input", "")
            print(f"fake-qwen: speech model={payload.get('model')} voice={payload.get('voice')} "
                  f"format={payload.get('response_format')} lang={payload.get('language')} "
                  f"instruct={payload.get('instruct')!r} chars={len(text)}", flush=True)
        elif self.path == "/tts":
            if TTS_ONLY and self.headers.get("X-API-Key") != API_KEY:
                self.reply(401, b'{"detail":"unauthorized"}', "application/json")
                return
            try:
                payload = json.loads(body)
            except json.JSONDecodeError:
                self.reply(400, b'{"detail":"bad json"}', "application/json")
                return
            text = payload.get("text", "")
            print(f"fake-qwen: tts prompt={payload.get('voice_prompt')!r} "
                  f"lang={payload.get('language')} key={self.headers.get('X-API-Key')} "
                  f"chars={len(text)}", flush=True)
        else:
            self.reply(404, b'{"detail":"not found"}', "application/json")
            return

        if "FAIL" in (text or ""):
            self.reply(500, b'{"error":"nope"}', "application/json")
            return

        audio = wav(tone(min(2.0, 0.4 + len(text or "") / 60.0)))
        self.reply(200, audio, "audio/wav")


def main() -> int:
    global NO_CLONE, TTS_ONLY
    NO_CLONE = "--no-clone" in sys.argv
    TTS_ONLY = "--tts-only" in sys.argv
    args = [arg for arg in sys.argv[1:] if not arg.startswith("--")]
    port = int(args[0]) if args else 8798

    server = ThreadingHTTPServer(("127.0.0.1", port), Handler)
    print(f"fake Qwen3-TTS on http://127.0.0.1:{port}"
          + (" (clone disabled)" if NO_CLONE else "")
          + (" (tts only, X-API-Key required)" if TTS_ONLY else ""), flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    sys.exit(main())
