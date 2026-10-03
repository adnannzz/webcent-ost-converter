using System.Text;
using OstConverter.Core.Export.Pst;

/// <summary>
/// Writes a small PST full of invented mail (fictional people, example.com addresses) so the app can be tried,
/// demonstrated and screenshotted without anybody's real mailbox. Usage: ostcli demo &lt;out.pst&gt;
/// </summary>
static class DemoMailbox
{
    static readonly (string Name, string Address)[] People =
    [
        ("Priya Nair", "priya.nair@example.com"), ("Daniel Okafor", "daniel.okafor@example.com"),
        ("Mei Tanaka", "mei.tanaka@example.com"), ("Lucas Moreau", "lucas.moreau@example.com"),
        ("Amara Singh", "amara.singh@example.com"), ("Tom Becker", "tom.becker@example.com"),
    ];

    static readonly string[] Subjects =
    [
        "Quarterly planning agenda", "Updated project timeline", "Invoice 2024-0187 attached", "Lunch on Thursday?",
        "Notes from today's call", "Draft proposal for review", "Welcome to the team", "Travel itinerary: Mumbai",
        "Re: contract wording", "Photos from the offsite", "Budget numbers, second pass", "Release checklist",
        "Minutes: design review", "Can you approve this?", "Out of office next week", "Thank you!",
    ];

    public static int Run(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: ostcli demo <out.pst>"); return 2; }
        var path = args[1];
        if (File.Exists(path)) { Console.Error.WriteLine("refusing to overwrite " + path); return 2; }
        var rnd = new Random(2024);
        using var b = new PstBuilder(path, "Demo Mailbox");
        var folders = new (string Path, int Count)[] { ("Inbox", 48), ("Sent Items", 28), ("Projects/Atlas launch", 36), ("Projects/Website refresh", 18) };
        int total = 0;
        foreach (var (folderPath, count) in folders)
        {
            var nid = b.EnsureFolder(folderPath, "IPF.Note");
            for (int i = 0; i < count; i++)
            {
                var person = People[rnd.Next(People.Length)];
                var subject = Subjects[rnd.Next(Subjects.Length)] + (i % 5 == 0 ? "" : $" ({i + 1})");
                var when = new DateTime(2024, 1, 1, 8, 0, 0, DateTimeKind.Utc).AddDays(rnd.Next(0, 330)).AddMinutes(rnd.Next(0, 600));
                var body = new StringBuilder()
                    .Append("Hi,\r\n\r\nThis is a made-up message used to demonstrate Webcent OST Converter. ")
                    .Append("Nothing in it is real.\r\n\r\n")
                    .Append($"Subject of the thread: {subject}\r\n\r\nBest regards,\r\n{person.Name}\r\n").ToString();
                var m = new PstMessageData();
                m.Props.Add(PstProp.Str(0x001A, "IPM.Note"));
                m.Props.Add(PstProp.Str(0x0037, subject));
                m.Props.Add(PstProp.Str(0x1000, body));
                m.Props.Add(PstProp.Int(0x0E07, rnd.Next(4) == 0 ? 0 : 1));
                m.Props.Add(PstProp.Time(0x0039, when));
                m.Props.Add(PstProp.Str(0x0C1A, person.Name));
                m.Recipients.Add([PstProp.Str(0x3001, "You"), PstProp.Str(0x3002, "SMTP"), PstProp.Str(0x3003, "you@example.com"), PstProp.Int(0x0C15, 1)]);
                b.AddMessage(nid, m);
                total++;
            }
        }
        b.Complete();
        Console.WriteLine($"wrote {total} invented messages to {path}");
        return 0;
    }
}
