using System.Reflection;

namespace DesktopCritters.Config;

/// <summary>
/// Where the widget keeps its editable files. Everything lives under
/// %APPDATA%\DesktopCritters so the built exe stays a single portable binary and
/// tuning a creature never means rebuilding.
/// </summary>
public static class Paths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopCritters");

    public static string ConfigFile => Path.Combine(Root, "config.json");
    public static string CreaturesDirectory => Path.Combine(Root, "creatures");

    /// <summary>Names of every creature file present, without the .json, sorted.</summary>
    public static List<string> AvailableCreatures()
    {
        if (!Directory.Exists(CreaturesDirectory))
            return new List<string>();

        return Directory.GetFiles(CreaturesDirectory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Creates the config tree on first run. Only ever writes files that are
    /// missing, so an edited creature is never clobbered by an upgrade.
    /// </summary>
    public static void EnsureSeeded()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(CreaturesDirectory);

        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith("creatures/", StringComparison.OrdinalIgnoreCase))
                continue;

            string target = Path.Combine(CreaturesDirectory, Path.GetFileName(resource));
            if (File.Exists(target))
                continue;

            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null) continue;

            using var file = File.Create(target);
            stream.CopyTo(file);
        }

        if (!File.Exists(ConfigFile))
            new AppConfig().Save(ConfigFile);
    }
}
