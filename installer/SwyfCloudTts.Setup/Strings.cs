using System.Globalization;

namespace SwyfCloudTts.Setup
{
    /// User-facing text in English, Ukrainian and Russian, picked from the Windows UI language.
    internal static class Strings
    {
        private static int _language;

        public static string Code => _language == 1 ? "uk" : _language == 2 ? "ru" : "en";

        public static void Init(string forced)
        {
            string code = string.IsNullOrEmpty(forced)
                ? CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
                : forced.ToLowerInvariant();
            _language = code == "uk" ? 1 : code == "ru" ? 2 : 0;
        }

        private static string T(string en, string uk, string ru) => _language == 1 ? uk : _language == 2 ? ru : en;

        public static string Subtitle => T(
            "Fast cloud voices for Scam With Your Friends",
            "Швидкі хмарні голоси для Scam With Your Friends",
            "Быстрые облачные голоса для Scam With Your Friends");

        public static string GameFolder => T("Game folder", "Тека гри", "Папка игры");

        public static string Browse => T("Browse…", "Огляд…", "Обзор…");

        public static string BrowseHint => T(
            "Choose the Scam With Your Friends folder",
            "Оберіть теку Scam With Your Friends",
            "Выберите папку Scam With Your Friends");

        public static string NotAGameFolder => T(
            "This folder does not contain Scam With Your Friends.",
            "У цій теці немає Scam With Your Friends.",
            "В этой папке нет Scam With Your Friends.");

        public static string StatusGameNotFound => T(
            "Game not found. Press Browse and choose the game folder.",
            "Гру не знайдено. Натисніть «Огляд» і вкажіть теку гри.",
            "Игра не найдена. Нажмите «Обзор» и укажите папку игры.");

        public static string StatusNotInstalled => T("Not installed", "Не встановлено", "Не установлено");

        public static string StatusInstalled => T("Installed, version {0}", "Встановлено, версія {0}", "Установлено, версия {0}");

        public static string StatusOutdated => T(
            "Version {0} is installed; this setup brings {1}",
            "Встановлено версію {0}; цей інсталятор містить {1}",
            "Установлена версия {0}; этот установщик содержит {1}");

        public static string StatusNeedsRepair => T(
            "Switched off by a game update. Press Repair.",
            "Вимкнено оновленням гри. Натисніть «Відновити».",
            "Отключено обновлением игры. Нажмите «Восстановить».");

        public static string CardInstalled => T(
            "Settings are in the game: Settings → Speech → CLOUD TTS.",
            "Налаштування — у грі: Settings → Speech → CLOUD TTS.",
            "Настройки — в игре: Settings → Speech → CLOUD TTS.");

        public static string CardMissing => T(
            "The in-game settings card is not installed.",
            "Картку налаштувань у грі не встановлено.",
            "Карточка настроек в игре не установлена.");

        public static string CardBlocked => T(
            "Another mod loader is installed, so the in-game settings card is skipped. Voices still work.",
            "Встановлено інший завантажувач модів, тому картку налаштувань у грі пропущено. Голоси працюють.",
            "Установлен другой загрузчик модов, поэтому карточка настроек в игре пропущена. Голоса работают.");

        public static string WhatItDoes => T(
            "Install puts the cloud voice worker into the game, keeping the original as a backup, and adds " +
            "a CLOUD TTS card to Settings → Speech. After a game update, open this setup again and press Repair.",
            "«Встановити» додає в гру хмарний озвучувач (оригінал зберігається як резервна копія) і картку " +
            "CLOUD TTS у Settings → Speech. Після оновлення гри відкрийте цей інсталятор і натисніть «Відновити».",
            "«Установить» добавляет в игру облачный озвучиватель (оригинал сохраняется как резервная копия) и карточку " +
            "CLOUD TTS в Settings → Speech. После обновления игры откройте этот установщик и нажмите «Восстановить».");

        public static string Install => T("Install", "Встановити", "Установить");

        public static string Update => T("Update", "Оновити", "Обновить");

        public static string Repair => T("Repair", "Відновити", "Восстановить");

        public static string Reinstall => T("Reinstall", "Перевстановити", "Переустановить");

        public static string Uninstall => T("Uninstall", "Видалити", "Удалить");

        public static string RemoveSettings => T(
            "When uninstalling, also delete my settings, API keys and character voices",
            "Під час видалення також стерти налаштування, API-ключі та голоси персонажів",
            "При удалении также стереть настройки, API-ключи и голоса персонажей");

        public static string OpenSettingsFolder => T("Open settings folder", "Відкрити теку налаштувань", "Открыть папку настроек");

        public static string ConfirmUninstall => T(
            "Remove SWYF Cloud TTS from the game?",
            "Видалити SWYF Cloud TTS з гри?",
            "Удалить SWYF Cloud TTS из игры?");

        public static string UninstallPrompt => T(
            "Press Uninstall to remove SWYF Cloud TTS from the game.",
            "Натисніть «Видалити», щоб прибрати SWYF Cloud TTS з гри.",
            "Нажмите «Удалить», чтобы убрать SWYF Cloud TTS из игры.");

        public static string GameRunning => T(
            "Close the game first, then try again.",
            "Спершу закрийте гру, потім спробуйте ще раз.",
            "Сначала закройте игру, затем попробуйте снова.");

        public static string NeedAdmin => T(
            "Windows needs administrator rights to change files in the game folder. Continue?",
            "Windows потрібні права адміністратора, щоб змінити файли в теці гри. Продовжити?",
            "Windows нужны права администратора, чтобы изменить файлы в папке игры. Продолжить?");

        public static string AdminCancelled => T(
            "Administrator rights were not granted; nothing was changed.",
            "Права адміністратора не надано; нічого не змінено.",
            "Права администратора не предоставлены; ничего не изменено.");

        public static string StepBackup => T(
            "Saving a backup of the game's own voice worker…",
            "Зберігаю резервну копію ігрового озвучувача…",
            "Сохраняю резервную копию игрового озвучивателя…");

        public static string StepWorker => T(
            "Installing the cloud voice worker…",
            "Встановлюю хмарний озвучувач…",
            "Устанавливаю облачный озвучиватель…");

        public static string StepManifest => T(
            "Updating the game's file checksums…",
            "Оновлюю контрольні суми файлів гри…",
            "Обновляю контрольные суммы файлов игры…");

        public static string StepCard => T(
            "Installing the in-game settings card…",
            "Встановлюю картку налаштувань у грі…",
            "Устанавливаю карточку настроек в игре…");

        public static string StepAppFiles => T(
            "Saving helper files to your profile…",
            "Зберігаю допоміжні файли у профіль…",
            "Сохраняю вспомогательные файлы в профиль…");

        public static string StepRestore => T(
            "Restoring the game's own voice worker…",
            "Повертаю ігровий озвучувач…",
            "Возвращаю игровой озвучиватель…");

        public static string StepRemoveCard => T(
            "Removing the in-game settings card…",
            "Прибираю картку налаштувань із гри…",
            "Убираю карточку настроек из игры…");

        public static string StepCleanup => T("Cleaning up…", "Прибираю залишки…", "Убираю остатки…");

        public static string WarnNoBackup => T(
            "No backup of the original worker was found. To uninstall later, use Steam → Verify integrity of game files.",
            "Резервної копії оригінального озвучувача немає. Щоб потім видалити мод, скористайтеся Steam → Перевірити цілісність файлів.",
            "Резервной копии оригинального озвучивателя нет. Чтобы потом удалить мод, используйте Steam → Проверить целостность файлов.");

        public static string WarnRestoreWithSteam => T(
            "The original worker backup is missing. Restore it with Steam → Properties → Installed Files → Verify integrity.",
            "Резервної копії оригінального озвучувача немає. Відновіть її через Steam → Властивості → Встановлені файли → Перевірити цілісність.",
            "Резервной копии оригинального озвучивателя нет. Восстановите её через Steam → Свойства → Установленные файлы → Проверить целостность.");

        public static string DoneInstall => T(
            "Done! Start the game, open Settings → Speech → CLOUD TTS, pick a voice provider and paste your API key.",
            "Готово! Запустіть гру, відкрийте Settings → Speech → CLOUD TTS, оберіть провайдера голосу та вставте API-ключ.",
            "Готово! Запустите игру, откройте Settings → Speech → CLOUD TTS, выберите провайдера голоса и вставьте API-ключ.");

        public static string DoneUninstall => T(
            "Removed. The game uses its own voices again.",
            "Видалено. Гра знову говорить власними голосами.",
            "Удалено. Игра снова говорит своими голосами.");

        public static string Failed => T("Something went wrong: {0}", "Щось пішло не так: {0}", "Что-то пошло не так: {0}");

        public static string LogHint => T(
            "Details are saved in {0}",
            "Подробиці збережено в {0}",
            "Подробности сохранены в {0}");

        public static string DamagedSetup => T(
            "This setup file is damaged ({0} is missing). Download it again.",
            "Файл інсталятора пошкоджено (бракує {0}). Завантажте його ще раз.",
            "Файл установщика повреждён (нет {0}). Скачайте его заново.");

        public static string ManifestChanged => T(
            "The game's runtime-manifest.json has no entry for the voice worker. The game may have changed; check for a mod update.",
            "У runtime-manifest.json гри немає запису про озвучувач. Можливо, гра змінилася; перевірте оновлення мода.",
            "В runtime-manifest.json игры нет записи об озвучивателе. Возможно, игра изменилась; проверьте обновление мода.");
    }
}
