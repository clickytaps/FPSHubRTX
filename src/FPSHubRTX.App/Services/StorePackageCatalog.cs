using System.Collections;
using FPSHubRTX.Core.Platform;
using Windows.Management.Deployment;

namespace FPSHubRTX.App.Services;

/// <summary>
/// Lists the current user's installed Store/MSIX packages with the documented WinRT PackageManager API
/// (no admin rights needed). App ids are resolved lazily because only a few packages are ever launched.
/// </summary>
public sealed class StorePackageCatalog : IPackageCatalog
{
    private IReadOnlyList<InstalledPackage>? _cache;
    private DateTime _cachedAt;

    public IReadOnlyList<InstalledPackage> GetPackages()
    {
        if (_cache is not null && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(30)) return _cache;
        var list = new List<InstalledPackage>();
        try
        {
            var manager = new PackageManager();
            foreach (var p in manager.FindPackagesForUser(string.Empty))
            {
                try
                {
                    if (p.IsFramework || p.IsResourcePackage || p.IsBundle) continue;
                    string? location = null;
                    try { location = p.InstalledLocation?.Path; } catch (Exception) { }
                    string? display = null;
                    try { display = p.DisplayName; } catch (Exception) { }
                    var package = p;
                    list.Add(new InstalledPackage(p.Id.Name, p.Id.FamilyName, location, display, new LazyAppIds(() => AppIdsFor(package))));
                }
                catch (Exception)
                {
                    // A broken registration shouldn't stop the listing.
                }
            }
        }
        catch (Exception)
        {
            // PackageManager unavailable (very old Windows or policy) — Store games simply aren't listed.
        }
        _cache = list;
        _cachedAt = DateTime.UtcNow;
        return list;
    }

    private static List<string> AppIdsFor(Windows.ApplicationModel.Package package)
    {
        var ids = new List<string>();
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return ids;
            foreach (var entry in package.GetAppListEntries())
            {
                var aumid = entry.AppUserModelId; // "PackageFamilyName!AppId"
                var bang = aumid.IndexOf('!');
                if (bang > 0 && bang < aumid.Length - 1) ids.Add(aumid[(bang + 1)..]);
            }
        }
        catch (Exception)
        {
        }
        return ids;
    }

    private sealed class LazyAppIds : IReadOnlyList<string>
    {
        private readonly Lazy<List<string>> _ids;
        public LazyAppIds(Func<List<string>> factory) => _ids = new Lazy<List<string>>(factory);
        public string this[int index] => _ids.Value[index];
        public int Count => _ids.Value.Count;
        public IEnumerator<string> GetEnumerator() => _ids.Value.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
