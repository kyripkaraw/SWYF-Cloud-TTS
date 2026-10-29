using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SwyfCloudTts.Plugin
{
    /// Texts of the settings card in English, Ukrainian and Russian. The game itself has no
    /// Ukrainian, so "auto" follows the game when it runs in Russian and Windows otherwise.
    internal static class CloudTtsText
    {
        public static readonly string[] Settings = { "auto", "en", "uk", "ru" };

        private static readonly Dictionary<string, string[]> Table = new Dictionary<string, string[]>
        {
            // Header
            ["title"] = new[] { "CLOUD TTS", "ХМАРНА ОЗВУЧКА", "ОБЛАЧНАЯ ОЗВУЧКА" },
            ["intro"] = new[]
            {
                "Characters speak through a cloud voice service instead of the game's local Qwen model. " +
                "A voice chosen for a character in the list below wins over the global voice.",
                "Персонажі говорять через хмарний сервіс замість локальної моделі Qwen. " +
                "Голос, обраний для персонажа в списку нижче, важливіший за загальний.",
                "Персонажи говорят через облачный сервис вместо локальной модели Qwen. " +
                "Голос, выбранный для персонажа в списке ниже, важнее общего.",
            },
            ["configError"] = new[]
            {
                "The settings file could not be read, so editing is off. Fix or delete {0}, then reopen this menu. {1}",
                "Не вдалося прочитати файл налаштувань, тому редагування вимкнено. Виправте або видаліть {0} і відкрийте меню знову. {1}",
                "Не удалось прочитать файл настроек, поэтому редактирование выключено. Исправьте или удалите {0} и откройте меню снова. {1}",
            },

            // Panel language and the game's own Qwen preference
            ["language"] = new[] { "Panel language", "Мова панелі", "Язык панели" },
            ["language.auto"] = new[] { "Auto", "Авто", "Авто" },
            ["calls"] = new[] { "Cloud voices for", "Хмарні голоси для", "Облачные голоса для" },
            ["calls.nonEnglish"] = new[] { "All calls except English", "Усіх дзвінків, крім англійських", "Всех звонков, кроме английских" },
            ["calls.all"] = new[] { "All calls", "Усіх дзвінків", "Всех звонков" },
            ["calls.never"] = new[] { "Off (the game's own voices)", "Вимкнено (власні голоси гри)", "Выключено (собственные голоса игры)" },
            ["calls.help"] = new[]
            {
                "The game's own setting. By default English calls keep the game's built-in voice. Applies from the next call.",
                "Це налаштування самої гри. За замовчуванням англійські дзвінки озвучує вбудований голос гри. Діє з наступного дзвінка.",
                "Это настройка самой игры. По умолчанию английские звонки озвучивает встроенный голос игры. Действует со следующего звонка.",
            },
            ["calls.order"] = new[]
            {
                "The game picks the voice model for each call language in its own list in Settings → Speech (Pocket TTS, MOSS TTS Nano, Qwen3 TTS). " +
                "While this mod speaks, Qwen comes first for Russian; for other languages drag Qwen3 TTS to the top of that list. " +
                "Switching Qwen3 TTS off there keeps the game's own voice for that language.",
                "Гра обирає модель голосу для кожної мови дзвінка у власному списку в Settings → Speech (Pocket TTS, MOSS TTS Nano, Qwen3 TTS). " +
                "Поки говорить цей мод, для російської Qwen стоїть першим; для інших мов перетягніть Qwen3 TTS нагору цього списку. " +
                "Якщо вимкнути там Qwen3 TTS, для цієї мови лишиться власний голос гри.",
                "Игра выбирает модель голоса для каждого языка звонка в собственном списке в Settings → Speech (Pocket TTS, MOSS TTS Nano, Qwen3 TTS). " +
                "Пока говорит этот мод, для русского Qwen стоит первым; для других языков перетащите Qwen3 TTS наверх этого списка. " +
                "Если выключить там Qwen3 TTS, для этого языка останется собственный голос игры.",
            },
            ["game.noHardware"] = new[]
            {
                "The game does not use AI voices on this PC (Windows on ARM, or a hardware check the mod could not get past), so cloud voices cannot work here. Details are in plugin.log.",
                "На цьому ПК гра не вмикає AI-голоси (Windows на ARM або перевірка заліза, яку мод не зміг обійти), тому хмарні голоси тут не працюватимуть. Подробиці в plugin.log.",
                "На этом ПК игра не включает AI-голоса (Windows на ARM или проверка железа, которую мод не смог обойти), поэтому облачные голоса здесь не заработают. Подробности в plugin.log.",
            },
            ["game.downloading"] = new[]
            {
                "The game is still downloading its voice files (about 2 GB). Cloud voices start once the download is finished.",
                "Гра ще завантажує свої голосові файли (близько 2 ГБ). Хмарні голоси запрацюють, щойно завантаження завершиться.",
                "Игра ещё скачивает свои голосовые файлы (около 2 ГБ). Облачные голоса заработают, как только загрузка завершится.",
            },
            ["game.downloadFailed"] = new[]
            {
                "The game could not download its voice files, and it only calls the cloud after they are in place. Restart the game to retry.",
                "Гра не змогла завантажити свої голосові файли, а без них вона не звертається до хмари. Перезапустіть гру, щоб спробувати ще раз.",
                "Игра не смогла скачать свои голосовые файлы, а без них она не обращается к облаку. Перезапустите игру, чтобы попробовать снова.",
            },
            ["game.failed"] = new[]
            {
                "After a failed line the game switched cloud voices off until it restarts. Find the cause with Test voice, then restart the game.",
                "Після невдалої репліки гра вимкнула хмарні голоси до перезапуску. Знайдіть причину кнопкою «Перевірити голос» і перезапустіть гру.",
                "После неудачной реплики игра отключила облачные голоса до перезапуска. Найдите причину кнопкой «Проверить голос» и перезапустите игру.",
            },

            // Provider, key, model, voice
            ["provider"] = new[] { "Service", "Сервіс", "Сервис" },
            ["provider.help"] = new[]
            {
                "Apply saves the settings and restarts the voice worker; the game keeps running.",
                "«Застосувати» зберігає налаштування й перезапускає озвучувач; гру перезапускати не треба.",
                "«Применить» сохраняет настройки и перезапускает озвучку; игру перезапускать не нужно.",
            },
            ["provider.qwenremote"] = new[] { "Qwen3-TTS on your server", "Qwen3-TTS на вашому сервері", "Qwen3-TTS на вашем сервере" },
            ["provider.mock"] = new[] { "Test tones (offline)", "Тестові тони (без інтернету)", "Тестовые тона (без интернета)" },
            ["provider.silent"] = new[] { "Silence", "Тиша", "Тишина" },
            ["provider.passthrough"] = new[] { "The game's local model", "Локальна модель гри", "Локальная модель игры" },
            ["provider.changed"] = new[]
            {
                "Service changed. Apply saves it; Test voice tries it without saving.",
                "Сервіс змінено. «Застосувати» збереже його, а «Перевірити голос» спробує без збереження.",
                "Сервис изменён. «Применить» сохранит его, а «Проверить голос» попробует без сохранения.",
            },
            ["key"] = new[] { "Access key", "Ключ доступу", "Ключ доступа" },
            ["key.help"] = new[]
            {
                "Leave empty to keep the saved key. The key is stored only on this PC.",
                "Залиште порожнім, щоб не змінювати збережений ключ. Ключ зберігається лише на цьому ПК.",
                "Оставьте пустым, чтобы не менять сохранённый ключ. Ключ хранится только на этом ПК.",
            },
            ["key.clear"] = new[] { "Clear", "Стерти", "Стереть" },
            ["key.new"] = new[] { "New key, not saved yet.", "Новий ключ, ще не збережено.", "Новый ключ, ещё не сохранён." },
            ["key.saved"] = new[] { "Saved: ****{0}", "Збережено: ****{0}", "Сохранён: ****{0}" },
            ["key.savedEnv"] = new[] { " (+ environment variable)", " (+ змінна середовища)", " (+ переменная окружения)" },
            ["key.env"] = new[] { "Taken from an environment variable.", "Береться зі змінної середовища.", "Берётся из переменной окружения." },
            ["key.optional"] = new[]
            {
                "Optional: only if your server needs a token.",
                "Необов'язково: лише якщо сервер вимагає токен.",
                "Необязательно: только если серверу нужен токен.",
            },
            ["key.none"] = new[] { "No key saved.", "Ключ не збережено.", "Ключ не сохранён." },
            ["key.notUsed"] = new[] { "Not used.", "Не потрібен.", "Не нужен." },
            ["key.notService"] = new[]
            {
                "This service does not use a key.",
                "Цьому сервісу ключ не потрібен.",
                "Этому сервису ключ не нужен.",
            },
            ["key.cleared"] = new[] { "Key removed for {0}.", "Ключ для {0} видалено.", "Ключ для {0} удалён." },
            ["key.clearedEnv"] = new[]
            {
                " An environment variable still provides one.",
                " Змінна середовища все ще задає ключ.",
                " Переменная окружения всё ещё задаёт ключ.",
            },
            ["key.clearFailed"] = new[] { "Could not remove the key: {0}", "Не вдалося видалити ключ: {0}", "Не удалось удалить ключ: {0}" },
            ["model"] = new[] { "Model", "Модель", "Модель" },
            ["model.empty"] = new[] { "Model cannot be empty.", "Модель не може бути порожньою.", "Модель не может быть пустой." },
            ["custom"] = new[] { "Other (type it in)…", "Інше (ввести вручну)…", "Другое (ввести вручную)…" },
            ["model.fast"] = new[] { "fast, half price", "швидка, вдвічі дешевша", "быстрая, вдвое дешевле" },
            ["model.fastest"] = new[] { "fastest, half price", "найшвидша, вдвічі дешевша", "самая быстрая, вдвое дешевле" },
            ["model.best"] = new[] { "best quality", "найкраща якість", "лучшее качество" },
            ["model.expressive"] = new[] { "expressive", "виразна", "выразительная" },
            ["model.older"] = new[] { "older", "старіша", "старее" },
            ["voice"] = new[] { "Global voice", "Загальний голос", "Общий голос" },
            ["voice.auto"] = new[] { "Auto (own voice per character)", "Авто (свій голос кожному)", "Авто (свой голос каждому)" },
            ["voice.help"] = new[]
            {
                "For characters without their own choice. Auto gives every character a different voice; " +
                "a fixed voice makes everyone sound the same.",
                "Для персонажів без власного вибору. «Авто» дає кожному персонажу інший голос; " +
                "конкретний голос зробить усіх однаковими.",
                "Для персонажей без своего выбора. «Авто» даёт каждому персонажу другой голос; " +
                "конкретный голос сделает всех одинаковыми.",
            },
            ["voice.mine"] = new[] { "Load my voices", "Завантажити мої голоси", "Загрузить мои голоса" },
            ["voice.mineLoading"] = new[] { "Loading your voices…", "Завантажую ваші голоси…", "Загружаю ваши голоса…" },
            ["voice.mineLoaded"] = new[]
            {
                "Your voices loaded: {0}. They are in the voice lists now.",
                "Завантажено ваших голосів: {0}. Вони вже є у списках голосів.",
                "Загружено ваших голосов: {0}. Они уже есть в списках голосов.",
            },
            ["voice.mineNone"] = new[]
            {
                "The account has no voices of its own yet. Create one at elevenlabs.io: Voices → Create a voice → Voice Design, then load again.",
                "В акаунті ще немає власних голосів. Створіть голос на elevenlabs.io: Voices → Create a voice → Voice Design і завантажте ще раз.",
                "В аккаунте ещё нет своих голосов. Создайте голос на elevenlabs.io: Voices → Create a voice → Voice Design и загрузите снова.",
            },
            ["voice.mineFailed"] = new[] { "Could not load your voices: {0}", "Не вдалося завантажити ваші голоси: {0}", "Не удалось загрузить ваши голоса: {0}" },
            ["voice.mineLabel"] = new[] { "{0} (my voice)", "{0} (мій голос)", "{0} (мой голос)" },
            ["voice.clone"] = new[] { "Clone of the game voice", "Клон голосу з гри", "Клон голоса из игры" },
            ["voice.id"] = new[] { "Voice {0}", "Голос {0}", "Голос {0}" },
            ["voice.male"] = new[] { "male", "чоловічий", "мужской" },
            ["voice.female"] = new[] { "female", "жіночий", "женский" },
            ["voice.neutral"] = new[] { "neutral", "нейтральний", "нейтральный" },
            ["voice.old"] = new[] { "old", "літній", "пожилой" },
            ["voice.young"] = new[] { "young", "молодий", "молодой" },
            ["voice.british"] = new[] { "British", "британський", "британский" },

            // ElevenLabs
            ["eleven.clone"] = new[] { "Clone game voices", "Клонувати голоси з гри", "Клонировать голоса из игры" },
            ["eleven.clone.help"] = new[]
            {
                "Each character speaks with a copy of their own game voice (Starter plan or higher). When the voice slots " +
                "are full, the clone heard least recently is deleted; only voices named swyf-… are touched, and the last 5 " +
                "voice additions of the month stay yours. On the Free plan characters get default voices instead.",
                "Кожен персонаж говорить копією свого голосу з гри (тариф Starter або вищий). Коли слоти заповнені, " +
                "видаляється клон, якого найдовше не чули; мод чіпає лише голоси swyf-…, а останні 5 додавань голосів " +
                "за місяць лишає вам. На тарифі Free персонажі натомість отримують стандартні голоси.",
                "Каждый персонаж говорит копией своего голоса из игры (тариф Starter или выше). Когда слоты заполнены, " +
                "удаляется клон, которого дольше всех не слышали; мод трогает только голоса swyf-…, а последние 5 добавлений " +
                "голосов за месяц оставляет вам. На тарифе Free персонажи вместо этого получают стандартные голоса.",
            },
            ["eleven.account"] = new[]
            {
                "Account (as of {0}): {1}/{2} voice slots.",
                "Акаунт (станом на {0}): слоти {1}/{2}.",
                "Аккаунт (на {0}): слоты {1}/{2}.",
            },
            ["eleven.accountBudget"] = new[]
            {
                "Account (as of {0}): {1}/{2} voice slots, {3}/{4} voice additions this month.",
                "Акаунт (станом на {0}): слоти {1}/{2}, додавань голосів цього місяця {3}/{4}.",
                "Аккаунт (на {0}): слоты {1}/{2}, добавлений голосов в этом месяце {3}/{4}.",
            },
            ["eleven.paused"] = new[] { "Cloning paused until {0}: {1}.", "Клонування на паузі до {0}: {1}.", "Клонирование на паузе до {0}: {1}." },
            ["block.plan"] = new[]
            {
                "this plan cannot clone voices, so characters use default voices",
                "цей тариф не клонує голоси, тому персонажі говорять стандартними голосами",
                "этот тариф не клонирует голоса, поэтому персонажи говорят стандартными голосами",
            },
            ["block.budget"] = new[]
            {
                "the monthly voice additions are used up (5 are kept for you)",
                "місячні додавання голосів вичерпано (5 лишено вам)",
                "месячные добавления голосов исчерпаны (5 оставлено вам)",
            },
            ["block.slots"] = new[]
            {
                "every voice slot holds a voice the mod may not delete (your own voices or characters set to Keep)",
                "усі слоти зайняті голосами, які мод не може видаляти (ваші власні голоси або персонажі з «Не видаляти»)",
                "все слоты заняты голосами, которые мод не может удалять (ваши собственные голоса или персонажи с «Не удалять»)",
            },
            ["block.key"] = new[]
            {
                "the access key lacks the Voices permission (ElevenLabs → Developers → API keys)",
                "ключу бракує дозволу Voices (ElevenLabs → Developers → API keys)",
                "ключу не хватает разрешения Voices (ElevenLabs → Developers → API keys)",
            },
            ["stability"] = new[] { "Stability", "Стабільність", "Стабильность" },
            ["similarity"] = new[] { "Similarity", "Схожість", "Сходство" },

            // Google AI, OpenAI, self-hosted Qwen
            ["google.style"] = new[] { "Voice style", "Стиль голосу", "Стиль голоса" },
            ["google.style.help"] = new[]
            {
                "A style prompt for Gemini, for example: calm, warm, slow.",
                "Підказка стилю для Gemini, наприклад: спокійно, тепло, повільно.",
                "Подсказка стиля для Gemini, например: спокойно, тепло, медленно.",
            },
            ["openai.instructions"] = new[] { "Delivery", "Манера мовлення", "Манера речи" },
            ["openai.instructions.help"] = new[]
            {
                "Optional tone and delivery instructions for gpt-4o-mini-tts.",
                "Необов'язкові вказівки щодо тону й подачі для gpt-4o-mini-tts.",
                "Необязательные указания по тону и подаче для gpt-4o-mini-tts.",
            },
            ["speed"] = new[] { "Speed", "Швидкість", "Скорость" },
            ["qwen.url"] = new[] { "Server address", "Адреса сервера", "Адрес сервера" },
            ["qwen.url.help"] = new[]
            {
                "The Qwen3-TTS API on your GPU machine or rented pod, for example http://127.0.0.1:8000.",
                "Qwen3-TTS API на вашій машині з GPU або орендованому сервері, наприклад http://127.0.0.1:8000.",
                "Qwen3-TTS API на вашей машине с GPU или арендованном сервере, например http://127.0.0.1:8000.",
            },
            ["qwen.urlEmpty"] = new[] { "The server address cannot be empty.", "Адреса сервера не може бути порожньою.", "Адрес сервера не может быть пустым." },
            ["qwen.urlScheme"] = new[]
            {
                "The server address must start with http:// or https://.",
                "Адреса сервера має починатися з http:// або https://.",
                "Адрес сервера должен начинаться с http:// или https://.",
            },
            ["qwen.mode"] = new[] { "Voice source", "Джерело голосу", "Источник голоса" },
            ["qwen.mode.clone"] = new[] { "Clone the game voice", "Клон голосу з гри", "Клон голоса из игры" },
            ["qwen.mode.voice"] = new[] { "A voice on the server", "Голос на сервері", "Голос на сервере" },
            ["qwen.mode.design"] = new[] { "A voice from a description", "Голос з опису", "Голос из описания" },
            ["qwen.mode.help"] = new[]
            {
                "Cloning keeps every character's game voice. A server voice is faster: put its name in Global voice. " +
                "A voice from a description needs a server with POST /tts and uses Delivery hint as the prompt.",
                "Клон зберігає голос кожного персонажа з гри. Голос на сервері швидший: впишіть його назву в «Загальний голос». " +
                "Голос з опису потребує сервера з POST /tts і бере «Підказку подачі» як опис.",
                "Клон сохраняет голос каждого персонажа из игры. Голос на сервере быстрее: впишите его имя в «Общий голос». " +
                "Голос из описания требует сервера с POST /tts и берёт «Подсказку подачи» как описание.",
            },
            ["qwen.voiceNeeded"] = new[]
            {
                "The server voice mode needs a voice name in Global voice.",
                "Для голосу на сервері впишіть його назву в «Загальний голос».",
                "Для голоса на сервере впишите его имя в «Общий голос».",
            },
            ["qwen.language"] = new[] { "Speech language", "Мова мовлення", "Язык речи" },
            ["qwen.language.auto"] = new[] { "Same as the call", "Як у дзвінку", "Как в звонке" },
            ["qwen.instruct"] = new[] { "Delivery hint", "Підказка подачі", "Подсказка подачи" },
            ["qwen.instruct.help"] = new[]
            {
                "Optional, for example: warm, cheerful, slightly fast. Most servers ignore it when cloning.",
                "Необов'язково, наприклад: тепло, бадьоро, трохи швидко. Під час клонування більшість серверів це ігнорує.",
                "Необязательно, например: тепло, бодро, чуть быстрее. При клонировании большинство серверов это игнорирует.",
            },

            // Network and cache
            ["network"] = new[] { "NETWORK & CACHE", "МЕРЕЖА І КЕШ", "СЕТЬ И КЕШ" },
            ["timeout"] = new[] { "Wait for audio", "Очікування звуку", "Ожидание звука" },
            ["seconds"] = new[] { "{0} s", "{0} с", "{0} с" },
            ["timeout.help"] = new[]
            {
                "Seconds without audio before a request is retried. The first audio of a line must arrive within 15 s: " +
                "the game turns cloud voices off until restart when it waits more than 20 s.",
                "Скільки секунд без звуку чекати, перш ніж повторити запит. Перший звук репліки має прийти за 15 с: " +
                "якщо гра чекає понад 20 с, вона вимикає хмарні голоси до перезапуску.",
                "Сколько секунд без звука ждать, прежде чем повторить запрос. Первый звук реплики должен прийти за 15 с: " +
                "если игра ждёт дольше 20 с, она отключает облачные голоса до перезапуска.",
            },
            ["attempts"] = new[] { "Attempts", "Спроби", "Попытки" },
            ["fallback"] = new[] { "Game model as backup", "Модель гри як запас", "Модель игры про запас" },
            ["fallback.help"] = new[]
            {
                "When the cloud fails right away, try the game's own local model. On weak PCs it rarely answers in time.",
                "Якщо хмара одразу дала збій, спробувати власну локальну модель гри. На слабких ПК вона рідко встигає.",
                "Если облако сразу дало сбой, попробовать собственную локальную модель игры. На слабых ПК она редко успевает.",
            },
            ["cache"] = new[] { "Audio cache", "Кеш звуку", "Кеш звука" },
            ["cache.max"] = new[] { "Cache limit, MB", "Ліміт кешу, МБ", "Лимит кеша, МБ" },
            ["cache.clearOnChange"] = new[] { "Clear cache on voice change", "Чистити кеш при зміні голосу", "Чистить кеш при смене голоса" },
            ["cache.clear"] = new[] { "Clear cache", "Очистити кеш", "Очистить кеш" },
            ["cache.cleared"] = new[] { "Cached clips removed: {0}.", "Видалено записів із кешу: {0}.", "Удалено записей из кеша: {0}." },
            ["on"] = new[] { "On", "Увімкнено", "Включено" },
            ["off"] = new[] { "Off", "Вимкнено", "Выключено" },

            // Test and apply
            ["test"] = new[] { "TEST", "ПЕРЕВІРКА", "ПРОВЕРКА" },
            ["test.phrase"] = new[] { "Test phrase", "Фраза для перевірки", "Фраза для проверки" },
            ["test.phrase.help"] = new[]
            {
                "Empty uses a built-in phrase. Test voice uses the values above without saving them.",
                "Порожнє поле — вбудована фраза. «Перевірити голос» бере значення вище, не зберігаючи їх.",
                "Пустое поле — встроенная фраза. «Проверить голос» берёт значения выше, не сохраняя их.",
            },
            ["test.default"] = new[]
            {
                "Hello! This is a cloud voice test.",
                "Привіт! Це перевірка хмарного голосу.",
                "Привет! Это проверка облачного голоса.",
            },
            ["apply"] = new[] { "Apply", "Застосувати", "Применить" },
            ["test.button"] = new[] { "Test voice", "Перевірити голос", "Проверить голос" },
            ["saved"] = new[] { "Saved.", "Збережено.", "Сохранено." },
            ["saved.cache"] = new[] { " Cached clips removed: {0}.", " Видалено записів із кешу: {0}.", " Удалено записей из кеша: {0}." },
            ["saved.reloaded"] = new[]
            {
                " The voice worker restarted; the next line uses the new settings.",
                " Озвучувач перезапущено; наступна репліка вже з новими налаштуваннями.",
                " Озвучка перезапущена; следующая реплика уже с новыми настройками.",
            },
            ["saved.next"] = new[]
            {
                " The next line uses the new settings.",
                " Наступна репліка вже з новими налаштуваннями.",
                " Следующая реплика уже с новыми настройками.",
            },
            ["save.failed"] = new[] { "Could not save: {0}", "Не вдалося зберегти: {0}", "Не удалось сохранить: {0}" },
            ["test.noKey"] = new[]
            {
                "Enter an access key first, or save one with Apply.",
                "Спершу введіть ключ доступу або збережіть його кнопкою «Застосувати».",
                "Сначала введите ключ доступа или сохраните его кнопкой «Применить».",
            },
            ["test.running"] = new[] { "Testing {0}…", "Перевіряю {0}…", "Проверяю {0}…" },
            ["test.cancelled"] = new[] { "Test cancelled.", "Перевірку скасовано.", "Проверка отменена." },
            ["test.failed"] = new[] { "Test failed: {0}", "Перевірка не вдалася: {0}", "Проверка не удалась: {0}" },
            ["test.ok"] = new[]
            {
                "Test passed: playing through the game audio.",
                "Перевірка пройшла: звук грає через гру.",
                "Проверка прошла: звук играет через игру.",
            },
            ["test.playFailed"] = new[]
            {
                "Audio was generated, but playback failed: {0}",
                "Звук згенеровано, але відтворити не вдалося: {0}",
                "Звук сгенерирован, но воспроизвести не удалось: {0}",
            },
            ["test.passthrough"] = new[]
            {
                "The game's local model cannot be tested from here.",
                "Локальну модель гри звідси перевірити не можна.",
                "Локальную модель игры отсюда проверить нельзя.",
            },
            ["test.startFailed"] = new[] { "Could not start the test: {0}", "Не вдалося почати перевірку: {0}", "Не удалось начать проверку: {0}" },
            ["worker.missing"] = new[]
            {
                "The voice worker was not found. Run the SWYF Cloud TTS setup again.",
                "Озвучувач не знайдено. Запустіть інсталятор SWYF Cloud TTS ще раз.",
                "Озвучка не найдена. Запустите установщик SWYF Cloud TTS ещё раз.",
            },
            ["worker.timeout"] = new[]
            {
                "The service did not answer within two minutes.",
                "Сервіс не відповів за дві хвилини.",
                "Сервис не ответил за две минуты.",
            },

            // Characters
            ["chars"] = new[] { "CHARACTERS", "ПЕРСОНАЖІ", "ПЕРСОНАЖИ" },
            ["chars.count"] = new[] { "CHARACTERS ({0})", "ПЕРСОНАЖІ ({0})", "ПЕРСОНАЖИ ({0})" },
            ["chars.notUsed"] = new[]
            {
                "This service does not use character voices.",
                "Цей сервіс не використовує голоси персонажів.",
                "Этот сервис не использует голоса персонажей.",
            },
            ["chars.empty"] = new[]
            {
                "Characters appear here after they speak in the game. Each one is recognised by the voice clip the game sends.",
                "Персонажі з'являться тут, коли заговорять у грі. Кожного впізнають за голосовим кліпом, який надсилає гра.",
                "Персонажи появятся здесь, когда заговорят в игре. Каждого узнают по голосовому клипу, который присылает игра.",
            },
            ["chars.help.eleven"] = new[]
            {
                "Play lets you hear the game's own voice. Auto clones it when cloning is on; Keep stops the mod from ever deleting " +
                "that clone (for example the boss's). Your own voices appear in the lists after Load my voices.",
                "«Слухати» відтворює голос із гри. «Авто» клонує його, коли клонування увімкнене; «Не видаляти» забороняє моду " +
                "видаляти цей клон (наприклад, боса). Ваші власні голоси з'являться у списках після «Завантажити мої голоси».",
                "«Слушать» воспроизводит голос из игры. «Авто» клонирует его, когда клонирование включено; «Не удалять» запрещает моду " +
                "удалять этот клон (например, босса). Ваши собственные голоса появятся в списках после «Загрузить мои голоса».",
            },
            ["chars.help.qwen"] = new[]
            {
                "Type a server voice name per character. Empty clones the game clip in clone mode, otherwise uses the server voice.",
                "Впишіть назву голосу на сервері для персонажа. Порожнє поле в режимі клону клонує запис із гри, інакше бере голос сервера.",
                "Впишите имя голоса на сервере для персонажа. Пустое поле в режиме клона клонирует запись из игры, иначе берёт голос сервера.",
            },
            ["chars.help"] = new[]
            {
                "Play lets you hear the game's own voice. Auto uses the global voice when one is set, otherwise a voice " +
                "that matches the character's pitch. Names are only for you.",
                "«Слухати» відтворює голос із гри. «Авто» бере загальний голос, якщо його задано, інакше голос за висотою " +
                "голосу персонажа. Імена бачите лише ви.",
                "«Слушать» воспроизводит голос из игры. «Авто» берёт общий голос, если он задан, иначе голос по высоте " +
                "голоса персонажа. Имена видите только вы.",
            },
            ["chars.show"] = new[] { "Show list", "Показати список", "Показать список" },
            ["chars.hide"] = new[] { "Hide list", "Сховати список", "Скрыть список" },
            ["chars.refresh"] = new[] { "Refresh", "Оновити", "Обновить" },
            ["chars.resetAll"] = new[] { "Reset all voices", "Скинути всі голоси", "Сбросить все голоса" },
            ["chars.confirm"] = new[] { "Click again to reset", "Натисніть ще раз", "Нажмите ещё раз" },
            ["chars.play"] = new[] { "Play", "Слухати", "Слушать" },
            ["chars.rotate"] = new[] { "Rotate", "Ротація", "Ротация" },
            ["chars.keep"] = new[] { "Keep", "Не видаляти", "Не удалять" },
            ["chars.auto"] = new[] { "Auto", "Авто", "Авто" },
            ["chars.autoClone"] = new[] { "Auto (clone of the game voice)", "Авто (клон голосу з гри)", "Авто (клон голоса из игры)" },
            ["chars.male"] = new[] { "male", "чол.", "муж." },
            ["chars.female"] = new[] { "female", "жін.", "жен." },
            ["chars.unknown"] = new[] { "unknown", "невідомо", "неизвестно" },
            ["chars.hz"] = new[] { "{0} Hz", "{0} Гц", "{0} Гц" },
            ["chars.nameSaved"] = new[] { "Saved the name for {0}.", "Ім'я для {0} збережено.", "Имя для {0} сохранено." },
            ["chars.voiceSaved"] = new[]
            {
                "{0} speaks with {1} from the next line.",
                "{0} говоритиме голосом «{1}» з наступної репліки.",
                "{0} будет говорить голосом «{1}» со следующей реплики.",
            },
            ["chars.voiceAuto"] = new[] { "{0} is back to the automatic voice.", "{0} знову з автоматичним голосом.", "{0} снова с автоматическим голосом." },
            ["chars.saveFailed"] = new[] { "Could not save: {0}", "Не вдалося зберегти: {0}", "Не удалось сохранить: {0}" },
            ["chars.noClip"] = new[]
            {
                "No game clip is saved for this character.",
                "Для цього персонажа немає запису з гри.",
                "Для этого персонажа нет записи из игры.",
            },
            ["chars.playing"] = new[] { "Playing the game's own clip.", "Грає запис із гри.", "Играет запись из игры." },
            ["chars.playFailed"] = new[] { "Playback failed: {0}", "Не вдалося відтворити: {0}", "Не удалось воспроизвести: {0}" },
            ["chars.kept"] = new[]
            {
                "{0}: the clone will never be deleted.",
                "{0}: клон ніколи не видалятиметься.",
                "{0}: клон никогда не будет удаляться.",
            },
            ["chars.rotated"] = new[]
            {
                "{0}: the clone may be replaced again when slots run out.",
                "{0}: клон знову можна замінити, коли бракує слотів.",
                "{0}: клон снова можно заменить, когда не хватает слотов.",
            },
            ["chars.resetDone"] = new[]
            {
                "Characters back to the automatic voice: {0}.",
                "Повернуто до автоматичного голосу: {0}.",
                "Возвращено к автоматическому голосу: {0}.",
            },
            ["chars.resetFailed"] = new[] { "Could not reset the voices: {0}", "Не вдалося скинути голоси: {0}", "Не удалось сбросить голоса: {0}" },
            ["chars.error"] = new[]
            {
                "The character list could not be shown: {0}",
                "Не вдалося показати список персонажів: {0}",
                "Не удалось показать список персонажей: {0}",
            },
        };

        /// Resolved language of the card: "en", "uk" or "ru".
        public static string Current { get; private set; } = "en";

        public static void Use(string setting) => Current = Resolve(setting);

        public static string Resolve(string setting)
        {
            switch ((setting ?? "").Trim().ToLowerInvariant())
            {
                case "en": return "en";
                case "uk": return "uk";
                case "ru": return "ru";
            }

            if (CloudTtsGame.LanguageCode == "ru")
            {
                return "ru";
            }

            try
            {
                if (Application.systemLanguage == SystemLanguage.Ukrainian)
                {
                    return "uk";
                }
            }
            catch
            {
                // Not available before the engine is up; English is fine then.
            }

            return "en";
        }

        public static string SettingLabel(string setting)
        {
            switch (setting)
            {
                case "en": return "English";
                case "uk": return "Українська";
                case "ru": return "Русский";
                default: return T("language.auto");
            }
        }

        public static string T(string key)
        {
            string[] texts;
            if (!Table.TryGetValue(key, out texts))
            {
                PluginLog.Debug("missing text: " + key);
                return key;
            }

            int index = Current == "uk" ? 1 : Current == "ru" ? 2 : 0;
            return texts[index];
        }

        public static string T(string key, params object[] args)
            => string.Format(CultureInfo.InvariantCulture, T(key), args);
    }
}
