using System.Globalization;

namespace MechanicaLauncher.Core.Localization;

public static class Locale
{
    private static Dictionary<string, string> _strings = new();
    private static string _currentLang = "en";

    public static string CurrentLanguage => _currentLang;

    public static void Init(string? overrideLang = null)
    {
        _currentLang = overrideLang ?? DetectLanguage();
        _strings = _currentLang switch
        {
            "ru" => Ru(),
            _ => En()
        };
    }

    public static string Get(string key) =>
        _strings.TryGetValue(key, out var val) ? val : key;

    private static string DetectLanguage()
    {
        var culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return culture switch
        {
            "ru" or "uk" or "be" => "ru",
            _ => "en"
        };
    }

    private static Dictionary<string, string> En() => new()
    {
        // Navigation
        ["nav.home"] = "Home",
        ["nav.instances"] = "Instances",
        ["nav.mods"] = "Mods",
        ["nav.account"] = "Account",
        ["nav.settings"] = "Settings",

        // Home
        ["home.play"] = "P L A Y",
        ["home.play_with"] = "PLAY  ·  {0}",
        ["home.kill"] = "KILL",
        ["home.running"] = "RUNNING",
        ["home.select_instance"] = "Select instance...",
        ["home.mods"] = "MODS",
        ["home.account"] = "ACCOUNT",
        ["home.instances"] = "INSTANCES",
        ["home.game_output"] = "Game Output",
        ["home.game_closed"] = "Game closed.",
        ["home.crashed"] = "Crashed (exit {0})",

        // Instances
        ["inst.title"] = "Instances",
        ["inst.new"] = "New Instance",
        ["inst.select"] = "Select",
        ["inst.selected"] = "Selected",
        ["inst.delete"] = "Delete Instance",
        ["inst.delete_confirm"] = "This will delete all mods, saves, and configs for this instance.",
        ["inst.create"] = "Create",
        ["inst.cancel"] = "Cancel",
        ["inst.name"] = "Name",
        ["inst.mc_version"] = "Minecraft Version",
        ["inst.loader"] = "Mod Loader",
        ["inst.none"] = "None (Vanilla)",
        ["inst.edit"] = "Edit Instance",
        ["inst.save"] = "Save",
        ["inst.no_instances"] = "No instances yet. Create one to get started!",
        ["inst.last_played"] = "Last played {0}",

        // Mods
        ["mods.title"] = "Mods",
        ["mods.installed"] = "INSTALLED",
        ["mods.search"] = "Search mods, shaders, modpacks...",
        ["mods.install"] = "Install",
        ["mods.no_instance"] = "Select an instance on the Home page first.",
        ["mods.no_mods"] = "No mods installed yet.",
        ["mods.no_results"] = "No results found.",
        ["mods.load_more"] = "Load More ({0} remaining)",
        ["mods.results"] = "{0} results",
        ["mods.downloads"] = "downloads",

        // Account
        ["acc.title"] = "Account",
        ["acc.ms_account"] = "Microsoft Account",
        ["acc.ms_desc"] = "Sign in to play on multiplayer servers, access Realms, and use your custom skin.",
        ["acc.ms_signin"] = "Sign in with Microsoft",
        ["acc.offline"] = "Play Offline",
        ["acc.offline_desc"] = "No auth needed. Multiplayer on licensed servers won't be available.",
        ["acc.nickname"] = "Your nickname...",
        ["acc.save"] = "Save",
        ["acc.signout"] = "Sign Out",
        ["acc.auth_failed"] = "Authorization Failed",
        ["acc.ok"] = "OK",

        // Settings
        ["set.title"] = "Settings",
        ["set.appearance"] = "Appearance",
        ["set.theme"] = "Theme",
        ["set.dark"] = "Dark",
        ["set.light"] = "Light",
        ["set.system"] = "System",
        ["set.launcher"] = "Launcher",
        ["set.close_on_launch"] = "Close launcher when game starts",
        ["set.show_snapshots"] = "Show snapshot versions in instance creation",
        ["set.language"] = "Language",
        ["set.checking_updates"] = "Checking for updates...",
        ["set.update_available"] = "Update available: v{0}",
        ["set.download_update"] = "Download Update",
        ["set.up_to_date"] = "You're up to date!",
        ["set.update_check_failed"] = "Could not check for updates",

        // TLauncher
        ["tl.title"] = "TLAUNCHER DETECTED",
        ["tl.desc"] = "TLauncher was found on your computer.\n\nTLauncher is a launcher that was taken from its original developer. The new owners bundle their own Java, download unknown files from unknown servers, and collect data about your PC. Antivirus software flags it as a threat.\n\nDon't worry — we'll remove all this junk and move your worlds and mods to a safe launcher.",
        ["tl.threats"] = "Found: {0} threats",
        ["tl.timer"] = "Buttons unlock in {0} sec...",
        ["tl.accept"] = "Agree — clean PC",
        ["tl.decline"] = "Decline",
        ["tl.scanning"] = "Scanning worlds and mods...",
        ["tl.migrating"] = "Migrating data...",
        ["tl.migration_done"] = "Migration complete!",
        ["tl.removing"] = "Removing TLauncher files...",
        ["tl.registry"] = "Cleaning registry...",
        ["tl.almost"] = "Almost done...",
        ["tl.done"] = "Done! Your PC is clean.",

        // General
        ["gen.error"] = "Error",
        ["gen.delete"] = "Delete",
    };

    private static Dictionary<string, string> Ru() => new()
    {
        // Navigation
        ["nav.home"] = "Главная",
        ["nav.instances"] = "Инстансы",
        ["nav.mods"] = "Моды",
        ["nav.account"] = "Аккаунт",
        ["nav.settings"] = "Настройки",

        // Home
        ["home.play"] = "И Г Р А Т Ь",
        ["home.play_with"] = "ИГРАТЬ  ·  {0}",
        ["home.kill"] = "СТОП",
        ["home.running"] = "ЗАПУЩЕНО",
        ["home.select_instance"] = "Выбрать инстанс...",
        ["home.mods"] = "МОДЫ",
        ["home.account"] = "АККАУНТ",
        ["home.instances"] = "ИНСТАНСЫ",
        ["home.game_output"] = "Вывод игры",
        ["home.game_closed"] = "Игра закрыта.",
        ["home.crashed"] = "Крашнулась (код {0})",

        // Instances
        ["inst.title"] = "Инстансы",
        ["inst.new"] = "Новый инстанс",
        ["inst.select"] = "Выбрать",
        ["inst.selected"] = "Выбран",
        ["inst.delete"] = "Удалить инстанс",
        ["inst.delete_confirm"] = "Все моды, миры и настройки этого инстанса будут удалены.",
        ["inst.create"] = "Создать",
        ["inst.cancel"] = "Отмена",
        ["inst.name"] = "Название",
        ["inst.mc_version"] = "Версия Minecraft",
        ["inst.loader"] = "Мод-лоадер",
        ["inst.none"] = "Нет (Ванилла)",
        ["inst.edit"] = "Редактировать инстанс",
        ["inst.save"] = "Сохранить",
        ["inst.no_instances"] = "Пока нет инстансов. Создай первый!",
        ["inst.last_played"] = "Последний запуск {0}",

        // Mods
        ["mods.title"] = "Моды",
        ["mods.installed"] = "УСТАНОВЛЕНО",
        ["mods.search"] = "Поиск модов, шейдеров, модпаков...",
        ["mods.install"] = "Установить",
        ["mods.no_instance"] = "Сначала выбери инстанс на главной.",
        ["mods.no_mods"] = "Моды пока не установлены.",
        ["mods.no_results"] = "Ничего не найдено.",
        ["mods.load_more"] = "Загрузить ещё ({0} осталось)",
        ["mods.results"] = "{0} результатов",
        ["mods.downloads"] = "скачиваний",

        // Account
        ["acc.title"] = "Аккаунт",
        ["acc.ms_account"] = "Аккаунт Microsoft",
        ["acc.ms_desc"] = "Войди чтобы играть на серверах, использовать Realms и свой скин.",
        ["acc.ms_signin"] = "Войти через Microsoft",
        ["acc.offline"] = "Играть оффлайн",
        ["acc.offline_desc"] = "Без авторизации. Мультиплеер на лицензионных серверах недоступен.",
        ["acc.nickname"] = "Твой ник...",
        ["acc.save"] = "Сохранить",
        ["acc.signout"] = "Выйти",
        ["acc.auth_failed"] = "Ошибка авторизации",
        ["acc.ok"] = "ОК",

        // Settings
        ["set.title"] = "Настройки",
        ["set.appearance"] = "Внешний вид",
        ["set.theme"] = "Тема",
        ["set.dark"] = "Тёмная",
        ["set.light"] = "Светлая",
        ["set.system"] = "Системная",
        ["set.launcher"] = "Лаунчер",
        ["set.close_on_launch"] = "Закрывать лаунчер при запуске игры",
        ["set.show_snapshots"] = "Показывать снапшоты при создании инстанса",
        ["set.language"] = "Язык",
        ["set.checking_updates"] = "Проверяю обновления...",
        ["set.update_available"] = "Доступно обновление: v{0}",
        ["set.download_update"] = "Скачать обновление",
        ["set.up_to_date"] = "У тебя последняя версия!",
        ["set.update_check_failed"] = "Не удалось проверить обновления",

        // TLauncher
        ["tl.title"] = "ОБНАРУЖЕН TLAUNCHER",
        ["tl.desc"] = "На вашем компьютере обнаружен TLauncher.\n\nTLauncher — это лаунчер, который отжали у оригинального разработчика. Новые владельцы подсовывают свою Java, качают непонятно что с непонятных серверов и собирают данные о вашем ПК. Антивирусы флагают его как угрозу.\n\nНе парься — мы удалим всё это барахло и перетащим твои миры и моды в нормальный лаунчер.",
        ["tl.threats"] = "Найдено: {0} угроз",
        ["tl.timer"] = "Кнопки разблокируются через {0} сек...",
        ["tl.accept"] = "Согласен — очистить ПК",
        ["tl.decline"] = "Отказываюсь",
        ["tl.scanning"] = "Сканирую миры и моды...",
        ["tl.migrating"] = "Переношу данные...",
        ["tl.migration_done"] = "Перенос завершён!",
        ["tl.removing"] = "Удаляю файлы TLauncher...",
        ["tl.registry"] = "Чищу реестр...",
        ["tl.almost"] = "Почти готово...",
        ["tl.done"] = "Готово! Твой ПК чист.",

        // General
        ["gen.error"] = "Ошибка",
        ["gen.delete"] = "Удалить",
    };
}
