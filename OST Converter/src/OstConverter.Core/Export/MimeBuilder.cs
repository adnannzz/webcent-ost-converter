using MimeKit;
using OstConverter.Core.Pff;

namespace OstConverter.Core.Export;

/// <summary>Builds a standards-compliant MIME message from a decoded Outlook message.</summary>
public static class MimeBuilder
{
    const string UnknownAddress = "unknown@unknown.invalid";

    public static DateTime? MessageDate(Message m) => m.SentTime ?? m.ReceivedTime;

    public static MimeMessage Build(Message m, int depth = 0)
    {
        var msg = new MimeMessage();

        var fromAddr = Clean(m.SenderAddress);
        if (fromAddr.Length > 0 || m.SenderName.Length > 0)
            msg.From.Add(Mailbox(m.SenderName, fromAddr));

        foreach (var r in m.Recipients)
        {
            var box = Mailbox(r.Name, Clean(r.Address));
            switch (r.Kind)
            {
                case 2: msg.Cc.Add(box); break;
                case 3: msg.Bcc.Add(box); break;
                default: msg.To.Add(box); break;
            }
        }

        msg.Subject = m.Subject;
        var date = MessageDate(m);
        if (date is not null) msg.Date = new DateTimeOffset(DateTime.SpecifyKind(date.Value, DateTimeKind.Utc));
        else msg.Headers.Remove(HeaderId.Date);

        var id = m.InternetMessageId?.Trim().Trim('<', '>');
        if (!string.IsNullOrEmpty(id)) msg.MessageId = id;
        else msg.Headers.Remove(HeaderId.MessageId);

        ApplyTransportHeaders(m, msg);

        var builder = new BodyBuilder();
        var html = m.HtmlBody;
        var text = m.TextBody;
        if (string.IsNullOrEmpty(html) && string.IsNullOrEmpty(text))
        {
            var rtf = m.RtfBody;
            if (!string.IsNullOrEmpty(rtf)) text = RtfText.ToPlainText(rtf);
        }
        if (!string.IsNullOrEmpty(html)) builder.HtmlBody = html;
        if (!string.IsNullOrEmpty(text)) builder.TextBody = text;

        var extra = new List<MimeEntity>();
        foreach (var a in m.Attachments)
            AddAttachment(a, builder, extra, hasHtml: !string.IsNullOrEmpty(html), depth); // unreadable ones are skipped with a warning on the message

        var body = builder.ToMessageBody();
        if (extra.Count > 0)
        {
            var mixed = body as Multipart is { } mp && mp.ContentType.MimeType == "multipart/mixed" ? mp : new Multipart("mixed") { body };
            foreach (var e in extra) mixed.Add(e);
            body = mixed;
        }
        msg.Body = body ?? new TextPart("plain") { Text = "" };
        return msg;
    }

    static void AddAttachment(Attachment a, BodyBuilder builder, List<MimeEntity> extra, bool hasHtml, int depth)
    {
        if (a.IsEmbeddedMessage)
        {
            if (depth >= 4) return;
            var inner = a.TryOpenEmbeddedMessage();
            if (inner is null) return;
            extra.Add(new MessagePart("rfc822") { Message = Build(inner, depth + 1) });
            return;
        }

        var data = a.TryGetData();
        if (data is null) return;
        var name = a.FileName;
        var type = ContentType.TryParse(a.MimeType ?? "", out var ct) && ct.MediaType != "" ? ct
            : ContentType.Parse(MimeTypes.GetMimeType(name));

        if (hasHtml && a.IsInline && !string.IsNullOrEmpty(a.ContentId))
        {
            var res = builder.LinkedResources.Add(name, data, type);
            res.ContentId = a.ContentId!.Trim('<', '>');
        }
        else builder.Attachments.Add(name, data, type);
    }

    static void ApplyTransportHeaders(Message m, MimeMessage msg)
    {
        var raw = m.TransportHeaders;
        if (string.IsNullOrWhiteSpace(raw)) return;
        try
        {
            using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(raw.Replace("\r\n", "\n").Replace("\n", "\r\n") + "\r\n\r\n"));
            var headers = HeaderList.Load(ms);

            var irt = headers[HeaderId.InReplyTo];
            if (!string.IsNullOrWhiteSpace(irt)) msg.InReplyTo = irt.Trim().Trim('<', '>');
            var refs = headers[HeaderId.References];
            if (!string.IsNullOrWhiteSpace(refs))
                foreach (var r in refs.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                    msg.References.Add(r.Trim('<', '>'));
            var reply = headers[HeaderId.ReplyTo];
            if (!string.IsNullOrWhiteSpace(reply) && InternetAddressList.TryParse(reply, out var rl)) msg.ReplyTo.AddRange(rl);

            // Drafts and some imported items have no recipient table; fall back to the header values.
            if (msg.To.Count == 0 && InternetAddressList.TryParse(headers[HeaderId.To] ?? "", out var to)) msg.To.AddRange(to);
            if (msg.Cc.Count == 0 && InternetAddressList.TryParse(headers[HeaderId.Cc] ?? "", out var cc)) msg.Cc.AddRange(cc);
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException)
        {
            // Malformed headers are not worth failing the export for.
        }
    }

    static string Clean(string? address) => (address ?? "").Trim().Trim('<', '>').Replace(" ", "");

    static MailboxAddress Mailbox(string name, string address)
    {
        if (!address.Contains('@')) address = UnknownAddress;
        try { return new MailboxAddress(name ?? "", address); }
        catch (Exception e) when (e is ArgumentException or FormatException) { return new MailboxAddress(name ?? "", UnknownAddress); }
    }
}
