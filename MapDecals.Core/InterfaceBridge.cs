using Microsoft.Extensions.Logging;
using Sharp.Modules.AdminManager.Shared;
using Sharp.Modules.MenuManager.Shared;
using Sharp.Shared;
using Sharp.Shared.Managers;

namespace MapDecals;

/// <summary>
/// Holds engine managers and the cross-plugin interfaces MapDecals consumes. Optional interfaces
/// (AdminManager, MenuManager) are resolved in OnAllModulesLoaded — ModSharp guarantees all
/// publishers' PostInit finished by then.
/// </summary>
internal sealed class InterfaceBridge
{
    internal string SharpPath { get; }

    internal IModSharp           ModSharp           { get; }
    internal IClientManager      ClientManager      { get; }
    internal IEntityManager      EntityManager      { get; }
    internal IEventManager       EventManager       { get; }
    internal ITransmitManager    TransmitManager    { get; }
    internal ISharpModuleManager SharpModuleManager { get; }
    internal ILoggerFactory      LoggerFactory      { get; }

    // Resolved in OnAllModulesLoaded.
    internal IAdminManager? AdminManager { get; private set; }
    internal IMenuManager?  MenuManager  { get; private set; }

    public InterfaceBridge(string sharpPath, ISharedSystem sharedSystem)
    {
        SharpPath          = sharpPath;
        ModSharp           = sharedSystem.GetModSharp();
        ClientManager      = sharedSystem.GetClientManager();
        EntityManager      = sharedSystem.GetEntityManager();
        EventManager       = sharedSystem.GetEventManager();
        TransmitManager    = sharedSystem.GetTransmitManager();
        SharpModuleManager = sharedSystem.GetSharpModuleManager();
        LoggerFactory      = sharedSystem.GetLoggerFactory();
    }

    internal void ResolveModules()
    {
        AdminManager = SharpModuleManager
            .GetOptionalSharpModuleInterface<IAdminManager>(IAdminManager.Identity)?.Instance;
        MenuManager = SharpModuleManager
            .GetOptionalSharpModuleInterface<IMenuManager>(IMenuManager.Identity)?.Instance;
    }
}
