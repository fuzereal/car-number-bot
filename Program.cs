using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using System.Text;
using System.Text.RegularExpressions;


string BOT_TOKEN = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") ?? throw new Exception("");

// Настройка для GitHub Actions
bool IS_GITHUB_ACTIONS = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
string BOT_TOKEN = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") 
                   ?? throw new Exception("❌ TELEGRAM_BOT_TOKEN not found! Add it to GitHub Secrets");

if (IS_GITHUB_ACTIONS)
{
    Console.WriteLine("🎯 GITHUB ACTIONS MODE ACTIVATED");
    Console.WriteLine("=================================");
    Console.WriteLine($"🏷️  Repository: {Environment.GetEnvironmentVariable("GITHUB_REPOSITORY")}");
    Console.WriteLine($"🆔 Run ID: {Environment.GetEnvironmentVariable("GITHUB_RUN_ID")}");
    Console.WriteLine("⏰ Runtime: 5 hours 50 minutes");
    Console.WriteLine("🔄 Restart: Auto (every 5 hours)");
    Console.WriteLine("=================================");
}

// Глобальный обработчик исключений
AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
{
    Console.WriteLine($"💥 GLOBAL UNHANDLED EXCEPTION: {e.ExceptionObject}");
    if (IS_GITHUB_ACTIONS)
    {
        Console.WriteLine("🔄 Bot will restart automatically in 5 hours");
    }
    Environment.Exit(1);
};

TaskScheduler.UnobservedTaskException += (sender, e) =>
{
    Console.WriteLine($"💥 UNOBSERVED TASK EXCEPTION: {e.Exception}");
    e.SetObserved();
};

try
{
    Console.WriteLine($"🚀 Starting Telegram Bot at {DateTime.Now}");
    
    // Остальной ваш код запуска бота...
    var botClient = new TelegramBotClient(BOT_TOKEN);
    var me = await botClient.GetMeAsync();
    
    Console.WriteLine($"✅ Bot @{me.Username} started successfully");
    
    if (IS_GITHUB_ACTIONS)
    {
        Console.WriteLine("⏳ Running for 5 hours 50 minutes...");
        // Ждем 5 часов 50 минут перед graceful shutdown
        await Task.Delay(TimeSpan.FromHours(5).Add(TimeSpan.FromMinutes(50)));
        Console.WriteLine("🕒 Time limit reached, graceful shutdown");
        Environment.Exit(0);
    }
    else
    {
        // Локальный режим - бесконечный запуск
        await Task.Delay(-1);
    }
}
catch (Exception ex)
{
    Console.WriteLine($"💥 FATAL ERROR: {ex}");
    if (IS_GITHUB_ACTIONS)
    {
        Console.WriteLine("🔜 Auto-restart in 5 hours via GitHub Actions");
    }
    Environment.Exit(1);
}

var botClient = new TelegramBotClient(BOT_TOKEN);

try
{
    var me = await botClient.GetMeAsync();
    Console.WriteLine($" Бот @{me.Username} успешно запущен!");
}
catch (Exception ex)
{
    return;
}

var receiverOptions = new ReceiverOptions
{
    AllowedUpdates = []
};

botClient.StartReceiving(
    updateHandler: HandleUpdateAsync,
    pollingErrorHandler: HandlePollingErrorAsync,
    receiverOptions: receiverOptions
);

await Task.Delay(-1);

async Task HandleUpdateAsync(ITelegramBotClient client, Update update, CancellationToken ct)
{
    if (update.Message is not { Text: { } messageText } message)
        return;

    var chatId = message.Chat.Id;
    var userName = message.From?.FirstName ?? "Пользователь";

    Console.WriteLine($"📨 {userName}: {messageText}");

    if (messageText.StartsWith("/"))
    {
        switch (messageText)
        {
            case "/start":
                await SendWelcomeMessage(chatId, ct);
                break;
            case "/help":
                await SendHelpMessage(chatId, ct);
                break;
            default:
                await SendUnknownCommand(chatId, ct);
                break;
        }
    }
    else
    {
        await ProcessCarNumber(chatId, messageText, ct);
    }
}

Task HandlePollingErrorAsync(ITelegramBotClient client, Exception exception, CancellationToken ct)
{
    Console.WriteLine($" Ошибка: {exception.Message}");
    return Task.CompletedTask;
}

async Task SendWelcomeMessage(long chatId, CancellationToken ct)
{
    var text = """
     *Бот проверки автомобильных номеров*

    Отправьте мне номер автомобиля и я найду информацию

     *Примеры номеров:*
    • А123БВ78
    • А123БВ 78
    • х123хх777
    • е088та73

     *Если номер введен неправильно - парсинг автоматически отключится*
    """;

    await botClient.SendTextMessageAsync(
        chatId: chatId,
        text: text,
        parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
        cancellationToken: ct);
}

async Task ProcessCarNumber(long chatId, string number, CancellationToken ct)
{
    if (!IsValidCarNumber(number))
    {
        await botClient.SendTextMessageAsync(
            chatId: chatId,
            text: " *Неверный формат номера*\n\nПожалуйста, используйте формат: *А123БВ78*\n\nПримеры:\n• А123БВ78\n• Е001КХ178\n• Х123ХХ777",
            parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
            cancellationToken: ct);
        return;
    }

    try
    {
        var processingMessage = await botClient.SendTextMessageAsync(
            chatId: chatId,
            text: " Запускаем браузер для поиска информации...",
            cancellationToken: ct);

        var carInfo = await GetCarInfoWithSeleniumAsync(number);

        if (!carInfo.IsValid)
        {
            await botClient.EditMessageTextAsync(
                chatId: chatId,
                messageId: processingMessage.MessageId,
                text: $"❌ *Номер не найден*\n\nНомер `{number}` отсутствует в базе данных сайта",
                parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
                cancellationToken: ct);
            return;
        }

        var resultText = FormatCarInfo(carInfo);

        await botClient.EditMessageTextAsync(
            chatId: chatId,
            messageId: processingMessage.MessageId,
            text: resultText,
            parseMode: Telegram.Bot.Types.Enums.ParseMode.Markdown,
            cancellationToken: ct);

        if (!string.IsNullOrEmpty(carInfo.PhotoUrl) && carInfo.PhotoUrl != "Не указано")
        {
            try
            {
                await botClient.SendPhotoAsync(
                    chatId: chatId,
                    photo: InputFile.FromUri(carInfo.PhotoUrl),
                    caption: " Фото автомобиля",
                    cancellationToken: ct);

                Console.WriteLine($" Фото отправлено: {carInfo.PhotoUrl}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($" Не удалось отправить фото: {ex.Message}");
                await botClient.SendTextMessageAsync(
                    chatId: chatId,
                    text: $" Фото доступно по ссылке: {carInfo.PhotoUrl}",
                    cancellationToken: ct);
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($" Ошибка: {ex.Message}");
        await botClient.SendTextMessageAsync(
            chatId: chatId,
            text: $" Ошибка: {ex.Message}",
            cancellationToken: ct);
    }
}

async Task SendHelpMessage(long chatId, CancellationToken ct)
{
    await botClient.SendTextMessageAsync(
        chatId: chatId,
        text: " Просто отправьте номер автомобиля для проверки\n\n При неправильном номере парсинг автоматически отключится",
        cancellationToken: ct);
}

async Task SendUnknownCommand(long chatId, CancellationToken ct)
{
    await botClient.SendTextMessageAsync(
        chatId: chatId,
        text: "Используйте /help",
        cancellationToken: ct);
}

bool IsValidCarNumber(string text)
{
    if (string.IsNullOrWhiteSpace(text))
    {
        Console.WriteLine(" Пустой номер");
        return false;
    }

    var cleanNumber = text.Replace(" ", "").ToUpper();

    if (cleanNumber.Length < 6 || cleanNumber.Length > 9)
    {
        return false;
    }

    var hasLetters = cleanNumber.Any(char.IsLetter);
    var hasDigits = cleanNumber.Any(char.IsDigit);

    if (!hasLetters || !hasDigits)
    {
        Console.WriteLine($" Номер должен содержать и буквы и цифры");
        return false;
    }

    var russianLetters = "АВЕКМНОРСТУХ";
    var hasRussianLetters = cleanNumber.Any(c => russianLetters.Contains(c));

    if (!hasRussianLetters)
    {
        Console.WriteLine($"Номер должен содержать русские буквы");
        return false;
    }

    Console.WriteLine($"Номер прошел проверку: {cleanNumber}");
    return true;
}

async Task<CarInfo> GetCarInfoWithSeleniumAsync(string number)
{
    ChromeDriver? driver = null;
    
    try
    {
        Console.WriteLine("🚀 Запускаем Chrome браузер...");
        
        // Настройки Chrome для GitHub Actions
        var options = new ChromeOptions();
        
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        {
            Console.WriteLine("🔧 Настройка для GitHub Actions...");
            options.BinaryLocation = "/usr/bin/google-chrome";
        }
        
        // ОСНОВНЫЕ АРГУМЕНТЫ ДЛЯ РАБОТЫ В CI/CD
        options.AddArgument("--no-sandbox");
        options.AddArgument("--disable-dev-shm-usage");
        options.AddArgument("--disable-gpu");
        options.AddArgument("--headless=new");
        options.AddArgument("--window-size=1920,1080");
        
        // Убираем проблемы с user data directory
        options.AddArgument("--disable-features=VizDisplayCompositor");
        options.AddArgument("--disable-background-timer-throttling");
        options.AddArgument("--disable-backgrounding-occluded-windows");
        options.AddArgument("--disable-renderer-backgrounding");
        
        // Отключаем автоматизацию
        options.AddExcludedArgument("enable-automation");
        options.AddAdditionalOption("useAutomationExtension", false);
        
        // Устанавливаем User-Agent
        options.AddArgument("--user-agent=Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        // ВАЖНО: Указываем временную директорию для каждого запуска
        var tempUserDataDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        options.AddArgument($"--user-data-dir={tempUserDataDir}");
        
        Console.WriteLine($"📁 User data directory: {tempUserDataDir}");
        Console.WriteLine("🌐 Создаем Chrome драйвер...");
        
        // Настройки сервиса
        var service = ChromeDriverService.CreateDefaultService();
        service.HideCommandPromptWindow = true;
        
        // Запускаем драйвер
        driver = new ChromeDriver(service, options);
        
        // Устанавливаем таймауты
        driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(15);
        driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(45);
        driver.Manage().Timeouts().AsynchronousJavaScript = TimeSpan.FromSeconds(30);
        
        Console.WriteLine("✅ Chrome браузер успешно запущен");
        Console.WriteLine("🌐 Переходим на statenumber.ru...");
        
        try
        {
            driver.Navigate().GoToUrl("https://statenumber.ru/modules/catalog/");
            
            // Ждем загрузки страницы
            await Task.Delay(5000);
            
            // Проверяем что страница загрузилась
            if (driver.Title.Contains("error") || driver.Title.Contains("404"))
            {
                throw new Exception("Страница не загрузилась корректно");
            }
            
            Console.WriteLine($"✅ Страница загружена: {driver.Title}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠ Ошибка загрузки страницы: {ex.Message}");
            // Пробуем альтернативный URL
            driver.Navigate().GoToUrl("https://statenumber.ru/");
            await Task.Delay(3000);
        }
        
        // Ищем поле ввода номера
        Console.WriteLine("🔍 Ищем поле ввода номера...");
        IWebElement inputField;
        
        try
        {
            inputField = driver.FindElement(By.CssSelector("input.inputGosnomerVin[name='nomerGet']"));
            Console.WriteLine("✅ Поле найдено по основному селектору");
        }
        catch
        {
            try
            {
                // Альтернативный поиск
                inputField = driver.FindElement(By.CssSelector("input[name='nomerGet']"));
                Console.WriteLine("✅ Поле найдено по альтернативному селектору");
            }
            catch
            {
                // Последняя попытка - ищем любой input
                inputField = driver.FindElement(By.CssSelector("input[type='text']"));
                Console.WriteLine("✅ Поле найдено по общему селектору");
            }
        }
        
        // Очищаем поле и вводим номер
        inputField.Clear();
        inputField.SendKeys(number);
        Console.WriteLine($"📝 Ввели номер: {number}");
        
        // Ищем кнопку отправки
        IWebElement? submitButton = null;
        var buttonSelectors = new[]
        {
            "input[type='submit']",
            "button[type='submit']",
            ".btn",
            ".button",
            "button",
            "input.btn",
            "input[type='button']",
            "[onclick*='submit']",
            "form input[type='submit']",
            "form button"
        };
        
        foreach (var selector in buttonSelectors)
        {
            try
            {
                var buttons = driver.FindElements(By.CssSelector(selector));
                if (buttons.Count > 0)
                {
                    submitButton = buttons.First(b => b.Displayed && b.Enabled);
                    Console.WriteLine($"✅ Найдена кнопка с селектором: {selector}");
                    break;
                }
            }
            catch
            {
                // Игнорируем ошибки
            }
        }
        
        if (submitButton == null)
        {
            // Если кнопку не нашли, пробуем отправить через Enter
            Console.WriteLine("⌨ Отправляем через Enter...");
            inputField.SendKeys(Keys.Enter);
        }
        else
        {
            Console.WriteLine("🖱 Нажимаем кнопку отправки...");
            try
            {
                submitButton.Click();
            }
            catch
            {
                // Если клик не работает, пробуем через JavaScript
                ((IJavaScriptExecutor)driver).ExecuteScript("arguments[0].click();", submitButton);
                Console.WriteLine("✅ Кнопка нажата через JavaScript");
            }
        }
        
        // Ждем загрузки результатов
        Console.WriteLine("⏳ Ждем загрузки результатов...");
        await Task.Delay(8000);
        
        // Парсим результаты
        Console.WriteLine("🔍 Парсим результаты...");
        return ParseSeleniumResults(driver, number);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"💥 Ошибка в Selenium: {ex.Message}");
        Console.WriteLine($"💥 Stack trace: {ex.StackTrace}");
        throw new Exception($"Ошибка браузера: {ex.Message}");
    }
    finally
    {
        // Всегда закрываем браузер
        try
        {
            driver?.Quit();
            driver?.Dispose();
            Console.WriteLine("🔚 Браузер закрыт");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠ Ошибка при закрытии браузера: {ex.Message}");
        }
    }
}

CarInfo ParseSeleniumResults(IWebDriver driver, string number)
{
    var carInfo = new CarInfo { Number = number, IsValid = false };

    try
    {
        if (IsNumberNotFound(driver))
        {
            Console.WriteLine($" Номер {number} не найден в базе");
            carInfo.IsValid = false;
            return carInfo;
        }

        carInfo.Region = GetElementText(driver, "dataRegion");
        carInfo.VIN = GetElementText(driver, "dataVin");
        carInfo.Power = GetElementText(driver, "dataPower");
        carInfo.Color = GetElementText(driver, "dataColor");
        carInfo.Country = GetElementText(driver, "dataCountryBuild");

        carInfo.PhotoUrl = ExtractPhotoUrl(driver);

        if (carInfo.Region == "Не указано")
        {
            Console.WriteLine(" Пробуем альтернативный поиск...");

            carInfo.Region = FindByClass(driver, "resultAutoCardDataItemAnswer", "Регион");
            carInfo.Color = FindByClass(driver, "resultAutoCardDataItemAnswer", "Цвет");
            carInfo.Power = FindByClass(driver, "resultAutoCardDataItemAnswer", "Мощность");
            carInfo.VIN = FindByClass(driver, "resultAutoCardDataItemAnswer", "VIN");
        }

        var hasData = carInfo.Region != "Не указано" || carInfo.Color != "Не указаno" ||
                      carInfo.Power != "Не указано" || carInfo.VIN != "Не указано" ||
                      carInfo.Country != "Не указано" || carInfo.PhotoUrl != "Не указано";

        carInfo.IsValid = hasData;

        Console.WriteLine($" Найдены данные:");
        Console.WriteLine($"   Регион: {carInfo.Region}");
        Console.WriteLine($"   VIN: {carInfo.VIN}");
        Console.WriteLine($"   Мощность: {carInfo.Power}");
        Console.WriteLine($"   Цвет: {carInfo.Color}");
        Console.WriteLine($"   Страна: {carInfo.Country}");
        Console.WriteLine($"   Фото: {carInfo.PhotoUrl}");
        Console.WriteLine($"   Номер валиден: {carInfo.IsValid}");

        return carInfo;
    }
    catch (Exception ex)
    {
        Console.WriteLine($" Ошибка парсинга: {ex.Message}");
        carInfo.IsValid = false;
        return carInfo;
    }
}

bool IsNumberNotFound(IWebDriver driver)
{
    try
    {
        var errorSelectors = new[]
        {
            ".error",
            ".alert",
            ".message",
            ".resultAutoCardDataItemAnswer",
            "div[style*='display: none']",   
            "div:empty"                      
        };

        foreach (var selector in errorSelectors)
        {
            try
            {
                var elements = driver.FindElements(By.CssSelector(selector));
                foreach (var element in elements)
                {
                    var text = element.Text.Trim().ToLower();
                    if (text.Contains("не найден") || text.Contains("отсутствует") ||
                        text.Contains("ошибка") || text.Contains("error") ||
                        string.IsNullOrEmpty(text))
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }
        }

        var dataElements = driver.FindElements(By.CssSelector("#dataRegion, #dataVin, #dataPower, #dataColor, #dataCountryBuild"));
        if (dataElements.Count == 0)
        {
            Console.WriteLine("❌ Не найдено ни одного блока с данными");
            return true;
        }

        var allEmpty = true;
        foreach (var element in dataElements)
        {
            if (!string.IsNullOrEmpty(element.Text.Trim()))
            {
                allEmpty = false;
                break;
            }
        }

        if (allEmpty)
        {
            return true;
        }

        return false;
    }
    catch (Exception ex)
    {
        return false;
    }
}

string ExtractPhotoUrl(IWebDriver driver)
{
    try
    {
        var photoElements = driver.FindElements(By.CssSelector("div.resultAutoCardPhotoImg"));
        if (photoElements.Count == 0)
        {
            Console.WriteLine("Блок с фото не найден");
            return "Не указано";
        }

        var photoElement = photoElements[0];

        var styleAttribute = photoElement.GetAttribute("style");
        if (!string.IsNullOrEmpty(styleAttribute))
        {
            Console.WriteLine($"Найден style атрибут: {styleAttribute}");

            var urlMatch = Regex.Match(styleAttribute, @"url\(['""]?([^'""\)]+)['""]?\)");
            if (urlMatch.Success)
            {
                var photoUrl = urlMatch.Groups[1].Value;
                return photoUrl;
            }
        }

        var imgElements = photoElement.FindElements(By.TagName("img"));
        if (imgElements.Count > 0)
        {
            var imgSrc = imgElements[0].GetAttribute("src");
            if (!string.IsNullOrEmpty(imgSrc))
            {
                Console.WriteLine($"Найден URL фото в img: {imgSrc}");
                return imgSrc;
            }
        }

        var dataSrc = photoElement.GetAttribute("data-src");
        if (!string.IsNullOrEmpty(dataSrc))
        {
            return dataSrc;
        }

        return "Не указано";
    }
    catch (Exception ex)
    {
        return "Не указано";
    }
}

string GetElementText(IWebDriver driver, string elementId)
{
    try
    {
        var element = driver.FindElement(By.Id(elementId));
        var text = element.Text.Trim();
        return string.IsNullOrEmpty(text) ? "Не указано" : text;
    }
    catch
    {
        return "Не указано";
    }
}

string FindByClass(IWebDriver driver, string className, string title)
{
    try
    {
        var elements = driver.FindElements(By.ClassName(className));
        foreach (var element in elements)
        {
            var parent = element.FindElement(By.XPath("./.."));
            var parentText = parent.Text;

            if (parentText.Contains(title))
            {
                var text = element.Text.Trim();
                return string.IsNullOrEmpty(text) ? "Не указано" : text;
            }
        }
        return "Не указано";
    }
    catch
    {
        return "Не указано";
    }
}

string FormatCarInfo(CarInfo info)
{
    var builder = new StringBuilder();

    builder.AppendLine($"*Информация об автомобиле*");
    builder.AppendLine();

    builder.AppendLine($"*Номер:* `{info.Number}`");
    builder.AppendLine($"*Регион:* {info.Region}");
    builder.AppendLine($"*Страна:* {info.Country}");
    builder.AppendLine($"*Цвет:* {info.Color}");
    builder.AppendLine($"*Мощность:* {info.Power} л.с.");
    builder.AppendLine($"*VIN:* `{info.VIN}`");

    builder.AppendLine();

    if (!string.IsNullOrEmpty(info.PhotoUrl) && info.PhotoUrl != "Не указано")
    {
        builder.AppendLine(" *Фото автомобиля прилагается*");
    }



    return builder.ToString();
}

public class CarInfo
{
    public string Number { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string VIN { get; set; } = string.Empty;
    public string Power { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string PhotoUrl { get; set; } = string.Empty;
    public bool IsValid { get; set; } = true;
}

