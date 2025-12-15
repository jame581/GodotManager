using System;
using System.Collections.Generic;
using System.Linq;

namespace GodotManager.Domain;

internal sealed class InstallRegistry
{
    public List<InstallEntry> Installs { get; set; } = [];
    public Guid? ActiveId { get; set; }

    public void MarkActive(Guid id)
    {
        ActiveId = id;
        foreach (var install in Installs)
        {
            install.IsActive = install.Id == id;
        }
    }

    public InstallEntry? GetActive()
    {
        return ActiveId.HasValue ? Installs.FirstOrDefault(x => x.Id == ActiveId.Value) : null;
    }
}
