namespace EyeCtrl.Core.Models;

/// <summary>A launchable installed app: friendly display name + its AppUserModelID (AUMID).</summary>
public readonly record struct InstalledApp(string DisplayName, string Aumid);
