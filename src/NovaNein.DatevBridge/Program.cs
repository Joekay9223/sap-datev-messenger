using NovaNein.DatevBridge;

try
{
    if (args.Length == 0) return Usage();
    var command = args[0].ToLowerInvariant();
    var configPath = ValueAfter("--config") ?? Path.Combine(AppContext.BaseDirectory, "datev-bridge.json");
    if (command == "credentials")
    {
        var operation = args.Length > 1 ? args[1].ToLowerInvariant() : string.Empty;
        var target = ValueAfter("--target") ?? "NovaNein/DatevTransfer";
        if (operation == "check")
        {
            _ = WindowsCredentialStore.Read(target);
            Console.WriteLine("Der DATEV-Dateiserver-Zugang ist im Windows Credential Manager vorhanden.");
            return 0;
        }
        if (operation != "set") return Usage();
        var userName = ValueAfter("--username");
        if (string.IsNullOrWhiteSpace(userName))
        {
            Console.Write("DATEV-Dateiserver-Benutzername: ");
            userName = Console.ReadLine();
        }
        if (string.IsNullOrWhiteSpace(userName)) throw new InvalidDataException("Ein Benutzername ist erforderlich.");
        Console.Write("DATEV-Dateiserver-Kennwort (maskiert): ");
        var password = ReadPassword();
        WindowsCredentialStore.Write(target, userName, password);
        Console.WriteLine("Der DATEV-Dateiserver-Zugang wurde sicher gespeichert.");
        return 0;
    }
    if (command != "run-once") return Usage();
    var options = DatevBridgeOptions.Load(Path.GetFullPath(configPath));
    var count = new DatevBridgeRunner(options).RunOnce();
    Console.WriteLine($"Bridge-Durchlauf abgeschlossen; {count} Manifest(e) verarbeitet.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("DATEV-Bridge: " + ex.Message.Replace('\r', ' ').Replace('\n', ' '));
    return 1;
}

string? ValueAfter(string name)
{
    var index = Array.FindIndex(args, value => string.Equals(value, name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

string ReadPassword()
{
    var characters = new List<char>();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.WriteLine(); break; }
        if (key.Key == ConsoleKey.Backspace)
        {
            if (characters.Count > 0) characters.RemoveAt(characters.Count - 1);
            continue;
        }
        if (!char.IsControl(key.KeyChar)) characters.Add(key.KeyChar);
    }
    if (characters.Count == 0) throw new InvalidDataException("Ein Kennwort ist erforderlich.");
    return new string(characters.ToArray());
}

int Usage()
{
    Console.Error.WriteLine("Verwendung: NovaNein.DatevBridge run-once --config <datev-bridge.json>");
    Console.Error.WriteLine("             NovaNein.DatevBridge credentials set --target NovaNein/DatevTransfer [--username <Benutzer>]");
    Console.Error.WriteLine("             NovaNein.DatevBridge credentials check --target NovaNein/DatevTransfer");
    return 2;
}
