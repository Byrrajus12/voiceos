namespace VoiceOS.Core.Config;

public static class DotEnvLoader
{
    public static void Load(string? directory = null)
    {
        var dir = directory ?? AppContext.BaseDirectory;
        var path = Path.Combine(dir, ".env");

        if (!File.Exists(path))
        {
            var cwd = Directory.GetCurrentDirectory();
            path = Path.Combine(cwd, ".env");
        }

        if (!File.Exists(path)) return;

        foreach (var line in File.ReadAllLines(path))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#')) continue;

            var eq = trimmed.IndexOf('=');
            if (eq <= 0) continue;

            var key = trimmed[..eq].Trim();
            var value = trimmed[(eq + 1)..].Trim();

            if (string.IsNullOrEmpty(key)) continue;

            // Do not overwrite existing environment variables
            if (Environment.GetEnvironmentVariable(key) == null)
                Environment.SetEnvironmentVariable(key, value);
        }
    }
}
