using System.Net;
using EsimBot.BLL.DTO;
using Microsoft.Extensions.Options;
using QRCoder;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace EsimBot.BLL.Services;

public sealed class BotNotifier(ITelegramBotClient bot, IOptions<BotOptions> options, ILanguageService languages,
    IChatUiService? chatUi = null) : IBotNotifier
{
    public async Task DeliverAsync(Order order, StoredProfile profile, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(profile.ActivationCode) || profile.ActivationCode.Length > 2000)
            throw new InvalidOperationException("The activation code is missing or invalid.");
        var text = await languages.GetAsync(order.UserId, null, ct);

        // Generate the QR locally. The activation secret never goes to an external QR service.
        using var qr = QRCodeGenerator.GenerateQrCode(profile.ActivationCode, QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(qr);
        await using var stream = new MemoryStream(png.GetGraphic(10));
        if (chatUi is not null) await chatUi.ClearAsync(order.UserId, ct);
        var qrMessage = await bot.SendPhoto(order.UserId, InputFile.FromStream(stream, "esim.png"),
            caption: text.Text("notifications.ready_caption", Limit(order.PackageName, 160), order.Id),
            protectContent: true, cancellationToken: ct);
        if (chatUi is not null) await chatUi.TrackAsync(order.UserId, qrMessage.Id, replace: true, ct);

        var parts = profile.ActivationCode.Split('$');
        var manual = parts.Length >= 3 && parts[0].Equals("LPA:1", StringComparison.OrdinalIgnoreCase)
            ? text.Text("notifications.manual_parts", Html(parts[1]), Html(parts[2]))
            : text.Text("notifications.manual_code", Html(profile.ActivationCode));
        var apn = string.IsNullOrWhiteSpace(profile.Apn) ? "" : text.Text("notifications.apn", Html(Limit(profile.Apn, 120)));

        var instructions = await bot.SendMessage(order.UserId,
            text.Text("notifications.install", manual, apn),
            parseMode: ParseMode.Html, protectContent: true, cancellationToken: ct);
        if (chatUi is not null) await chatUi.TrackAsync(order.UserId, instructions.Id, replace: false, ct);
    }

    public async Task RefundedAsync(Order order, CancellationToken ct)
    {
        var text = await languages.GetAsync(order.UserId, null, ct);
        if (chatUi is null)
            await bot.SendMessage(order.UserId, text.Text("notifications.refunded", order.Id, order.Stars),
                protectContent: true, cancellationToken: ct);
        else
            await chatUi.ShowAsync(order.UserId, text.Text("notifications.refunded", order.Id, order.Stars),
                null, ParseMode.None, ct);
    }

    public async Task AlertAdminAsync(string orderId, string code, CancellationToken ct)
    {
        if (options.Value.AdminUserId <= 0) return;
        var text = await languages.GetAsync(options.Value.AdminUserId, null, ct);
        // Only a bounded internal status is included, never provider responses or credentials.
        var safeCode = new string(code.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').Take(60).ToArray());
        await bot.SendMessage(options.Value.AdminUserId,
            text.Text("notifications.admin_attention", Limit(orderId, 64), safeCode),
            protectContent: true, cancellationToken: ct);
    }

    internal static string Html(string text) => WebUtility.HtmlEncode(text);
    internal static string Limit(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
