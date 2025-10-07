using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using OpenQA.Selenium.Support.UI;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using System.Text;
using System.Text.RegularExpressions;


string BOT_TOKEN = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") 
                   ?? throw new Exception("");



var botClient = new TelegramBotClient(BOT_TOKEN);

try
{
    var me = await botClient.GetMeAsync();
    Console.WriteLine($" Бот @{me.Username}");

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
    Console.WriteLine($"{exception.Message}");
    return Task.CompletedTask;
}

async Task SendWelcomeMessage(long chatId, CancellationToken ct)
{
    var text = """

    Отправьте мне номер автомобиля и я найду информацию

     *Примеры номеров:*
    • А123БВ78
    • А123БВ 78
    • х123хх777
    • е088та73
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
            text: " Неверный формат номера. Пример: А123БВ78",
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

                
            }
            catch (Exception ex)
            {
                
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
        text: " Просто отправьте номер автомобиля для проверки",
        cancellationToken: ct);
}

async Task SendUnknownCommand(long chatId, CancellationToken ct)
{
    await botClient.SendTextMessageAsync(
        chatId: chatId,
        text: " Используйте /help для справки",
        cancellationToken: ct);
}


bool IsValidCarNumber(string text)
{
    if (string.IsNullOrWhiteSpace(text)) return false;

    var cleanNumber = text.Replace(" ", "").ToUpper();
    if (cleanNumber.Length < 6 || cleanNumber.Length > 9) return false;

    var hasLetters = cleanNumber.Any(char.IsLetter);
    var hasDigits = cleanNumber.Any(char.IsDigit);

    return hasLetters && hasDigits;
}

async Task<CarInfo> GetCarInfoWithSeleniumAsync(string number)
{
    ChromeDriver? driver = null;

    try
    {

        var options = new ChromeOptions();
        options.AddArgument("--disable-blink-features=AutomationControlled");
        options.AddArgument("--disable-infobars");
        options.AddArgument("--no-sandbox");
        options.AddArgument("--disable-dev-shm-usage");
        options.AddExcludedArgument("enable-automation");
        options.AddAdditionalOption("useAutomationExtension", false);

        driver = new ChromeDriver(options);

        driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(10);
        driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(30);

        driver.Navigate().GoToUrl("https://statenumber.ru/modules/catalog/");

        await Task.Delay(1000);

        Console.WriteLine(" Ищем поле ввода номера...");
        var inputField = driver.FindElement(By.CssSelector("input.inputGosnomerVin[name='nomerGet']"));

        inputField.Clear();
        inputField.SendKeys(number);
        Console.WriteLine($" Ввели номер: {number}");

        IWebElement? submitButton = null;

        var buttonSelectors = new[]
        {
            "input[type='submit']",
            "button[type='submit']",
            ".btn",
            ".button",
            "button",
            "input.btn"
        };

        foreach (var selector in buttonSelectors)
        {
            try
            {
                var buttons = driver.FindElements(By.CssSelector(selector));
                if (buttons.Count > 0)
                {
                    submitButton = buttons.First();
                    break;
                }
            }
            catch
            {
            }
        }

        if (submitButton == null)
        {
            
            inputField.SendKeys(Keys.Enter);
        }
        else
        {
            
            submitButton.Click();
        }

        
        await Task.Delay(500);

        try
        {
            var closeButtons = driver.FindElements(By.CssSelector(".close_modal_window, .modal-close, .close"));
            if (closeButtons.Count > 0)
            {
                
                closeButtons[0].Click();
                await Task.Delay(200);
            }
        }
        catch
        {
        }

        
        return ParseSeleniumResults(driver, number);
    }
    finally
    {
        driver?.Quit();
        driver?.Dispose();
        
    }
}

CarInfo ParseSeleniumResults(IWebDriver driver, string number)
{
    var carInfo = new CarInfo { Number = number };

    try
    {
        carInfo.Region = GetElementText(driver, "dataRegion");
        carInfo.VIN = GetElementText(driver, "dataVin");
        carInfo.Power = GetElementText(driver, "dataPower");
        carInfo.Color = GetElementText(driver, "dataColor");
        carInfo.Country = GetElementText(driver, "dataCountryBuild");

        carInfo.PhotoUrl = ExtractPhotoUrl(driver);

        if (carInfo.Region == "Не указано")
        {

            carInfo.Region = FindByClass(driver, "resultAutoCardDataItemAnswer", "Регион");
            carInfo.Color = FindByClass(driver, "resultAutoCardDataItemAnswer", "Цвет");
            carInfo.Power = FindByClass(driver, "resultAutoCardDataItemAnswer", "Мощность");
            carInfo.VIN = FindByClass(driver, "resultAutoCardDataItemAnswer", "VIN");
        }



        return carInfo;
    }
    catch (Exception ex)
    {
        return carInfo;
    }
}

string ExtractPhotoUrl(IWebDriver driver)
{
    try
    {
        // Ищем блок с фото
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

// Получение текста элемента по ID
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

    builder.AppendLine($" *Информация об автомобиле*");
    builder.AppendLine();

    builder.AppendLine($"*Номер:* `{info.Number}`");
    builder.AppendLine($"*Регион:* {info.Region}");
    builder.AppendLine($"*Страна:* {info.Country}");
    builder.AppendLine($"*Цвет:* {info.Color}");
    builder.AppendLine($"*Мощность:* {info.Power} л.с.");
    builder.AppendLine($"*VIN:* `{info.VIN}`");

    var hasData = info.Region != "Не указано" || info.Color != "Не указано" ||
                  info.Power != "Не указано" || info.VIN != "Не указано" || info.Country != "Не указано";

    if (!hasData)
    {
        builder.AppendLine();
        builder.AppendLine("*Данные не найдены*");
        builder.AppendLine("Неправильно введен номер");
    }

    builder.AppendLine();

    if (!string.IsNullOrEmpty(info.PhotoUrl) && info.PhotoUrl != "Не указано")
    {
        builder.AppendLine("*Фото автомобиля прилагается*");
    }


    return builder.ToString();
}

// Модель данных автомобиля
public class CarInfo
{
    public string Number { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string VIN { get; set; } = string.Empty;
    public string Power { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string PhotoUrl { get; set; } = string.Empty;

}

