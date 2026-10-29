# Сумісність із грою 0.1.95

Звіт за 28 жовтня 2026: Steam-збірка `26261495`, `Assembly-CSharp.dll` від 28.10.2026.
Звірено декомпіляцію 0.1.83 і 0.1.95 (`analysis/`, у репозиторій не потрапляє).

## Що зламалося

| Симптом | Причина в 0.1.95 | Виправлення |
|---|---|---|
| `Qwen voice redirected to the cloud: 5 of 6 hooks in place`, `hook LoadPrefix skipped` | `QwenWorkerClient.Load(talker, codec, backend)` став `Load(model, backend)`: модель тепер один GGUF | хук шукає обидві сигнатури й ставить `QwenWorkerClient.Backend = "cpu"` (гра пише його в лог) |
| Setup 1.4.0: «У runtime-manifest.json гри немає запису про озвучувач» | у записах маніфесту `"sha256"` тепер стоїть перед `"path"` | регулярний вираз інсталятора приймає обидва порядки |
| Хмарний воркер не пережив би першу репліку | протокол воркера 2 (див. нижче) | `App.cs` і `PassthroughClient` говорять обома протоколами |
| Російські дзвінки не доходять до мода | новий порядок моделей для кожної мови: для російської першим стоїть Pocket TTS | хук `SpeechModelPreferences.PrefersQwen` (див. нижче) |
| Рядок «Хмарні голоси для» у картці нічого не змінює | `SpeechProviderSelection.Qwen` втратив сеттер, а рядка гри `qwen-tts-dropdown` більше немає | рядок схований, натомість підказка про список моделей гри |

## Воркер, протокол 2

`QwenTtsWorker.exe` тепер маленький .NET-exe (11 КБ) над `audiocpp.dll` (audio.cpp v0.9.3, ABI 0.2)
з теками `cuda\` і `vulkan\`. Ім'я файлу й тека `kolkata-qwen-runtime` ті самі.

```
→ int32 1, string model, string backend          # backend: CPU | CUDA | Vulkan0
← int32 1, int32 2, string backend               # cpu | cuda | vulkan, інакше гра падає
→ int32 2, string text, string language, int32 refLen, float32[refLen]
                                                   # refLen = 0: той самий кліп, що в минулій репліці
← int32 2, int32 count, float32[count] … int32 3  # без змін
```

- Гра кешує декодований кліп кожного файлу й шле `refLen = 0`, коли наступна репліка має той самий
  кліп. Без збереженого кліпу оригінальний воркер відповідає `Missing Qwen voice reference.`, мод так само.
- Протокол визначається за другим рядком `Load`: у протоколі 2 там назва бекенду, у протоколі 1 шлях до кодека.
- `tools/probe.py --protocol 2` (типово) і `--protocol 1` перевіряють обидва варіанти.

## Маніфест і модель

- `runtime-manifest.json` має нові поля `source`, `commit`, `abi`, `version`, `protocol`, `extensionSha256`,
  `archives`. `QwenRuntimeFiles.Validate` тепер перевіряє `source = 0xShug0/audio.cpp`, `commit` і
  `protocol = 2`. Інсталятор змінює лише хеш воркера, тож ці поля лишаються.
- Модель: один файл `qwen3-tts-12hz-0.6b-base-q8_0.gguf` (1,99 ГБ) замість talker + tokenizer (1,28 ГБ),
  тека кешу `qwen3-tts\874fcb41…`. Хук `VerifyAsync` у BepInEx-збірці й далі пропускає завантаження.
- `HardwareEligible`: тепер потрібно 8 ГБ вільної RAM (було 6), для NVIDIA бекенд `CUDA`, ліміт пам'яті
  воркера 8 ГБ. Сигнатура `TryChooseBackend` не змінилась, обидва редиректи працюють як раніше.

## Порядок моделей для кожної мови

- Тип `Kolkata.Speech.SpeechModelPreferences` (у `Kolkata.Speech.dll`), ключ `MetaSave.Local`
  `Kolkata.SpeechModels.<code>`, значення на зразок `Pocket,Qwen,MossNano`, `!` перед моделлю означає «вимкнено».
  Редагується в грі: *Settings → Speech*, список моделей з перетягуванням.
- Без збереженого порядку для російської діє `Pocket, Qwen, MossNano`. Для інших мов Qwen перший лише для
  `zh`/`ja` (або для всіх, якщо стара `Kolkata.Qwen3Tts` = «завжди»).
- Гра питає Qwen, лише коли `QwenTtsProgress.HardwareSupported && PrefersQwen(language)` (першою
  ввімкненою стоїть Qwen), модель «готова» і `!QwenTtsSingleton.FailedThisSession`. Інакше спрацьовує
  перша ввімкнена модель не-Qwen (`LegacyModel`).

**Вибір:** патч `PrefersQwen`, а не підказка «перетягни Qwen нагору». Збережений порядок гравця не
змінюється, а відповідь рахує `CloudTtsModelOrder`:

| Провайдер | Мова | `PrefersQwen` |
|---|---|---|
| `passthrough` | будь-яка | як вирішила гра |
| будь-який інший | російська | `true`, якщо Qwen увімкнено в списку гри для цієї мови |
| `local` | мови з `local.languages` | так само: `true`, якщо Qwen увімкнено |
| `local` | інші | `false`: локальний сервер їх не вміє, говорить власний голос гри |
| хмарні | інші | як вирішила гра (гравець сам перетягує Qwen нагору) |

У BepInEx це Harmony-постфікс, у Doorstop-збірці той самий ручний перехід у пам'яті, що й для перевірки заліза.
Doorstop-редирект ставиться один раз при старті гри, тому з `passthrough` на інший провайдер треба
перезапустити гру (як і для перевірки заліза).

## Без змін

- `kolkata-voices` у `StreamingAssets`: 41 WAV і один MP4, ті самі файли від 21.10. Гра читає перші 10 с,
  кліпи не на 24 кГц (12 із 41: 44,1 і 48 кГц) переводить у 24 кГц WDL-ресемплером, стерео усереднює.
- Для російських дзвінків гра може взяти локалізований голос спільноти (`ResolveLocalizedVoice`), якого
  в `kolkata-voices` немає, тож id таких спікерів відомі лише з того, що гра надіслала.
- `PauseMenuToolkitController`, `speech-screen`, `QwenTtsSingleton.Instance`/`UnloadModel`: картка
  в Settings → Speech з'являється, як і раніше.

## Перевірено

- Збірки воркера, обох плагінів та інсталятора.
- `tools/probe.py` з `mock`: протокол 2 (`load ok … backend cpu`, повтори з `refLen = 0` беруть кліп із пам'яті)
  і протокол 1.
- Регулярний вираз інсталятора на справжньому маніфесті 0.1.95: знаходить хеш воркера, заміна змінює рівно один рядок.

Ще не перевірено в самій грі: хуки `Load` і `PrefersQwen` у BepInEx і редирект у Doorstop-збірці.
