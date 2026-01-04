using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Python.Runtime;
using Sharp.Shared;
using Sharp.Shared.Managers;
using Sharp.Shared.Types;
using Sharp.Shared.Enums;
using Sharp.Shared.Objects;
using Sharp.Shared.GameEntities;
using Sharp.Shared.GameEvents;
using Sharp.Extensions.GameEventManager;

namespace Sharp.Python;

public class PythonGameEventInfo
{
    public bool DontBroadcast { get; set; }

    public PythonGameEventInfo(bool dontBroadcast)
    {
        DontBroadcast = dontBroadcast;
    }
}

public sealed class PythonPluginLoader : IModSharpModule
{
    private readonly ISharedSystem _sharedSystem;
    private readonly string _sharpPath;
    private readonly List<dynamic> _loadedPlugins = new();
    private IntPtr _threadState;
    private IGameEventManager _gameEventManager;
    private IServiceProvider _serviceProvider;
    private IEntityManager _entityManager;

    public PythonPluginLoader(ISharedSystem sharedSystem,
                              string dllPath,
                              string sharpPath,
                              Version version,
                              IConfiguration coreConfiguration,
                              bool hotReload)
    {
        _sharedSystem = sharedSystem;
        _sharpPath = sharpPath;
    }

    public bool Init()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_sharedSystem);
        services.AddGameEventManager();
        _serviceProvider = services.BuildServiceProvider();
        _gameEventManager = _serviceProvider.GetRequiredService<IGameEventManager>();

        _entityManager = _sharedSystem.GetEntityManager();

        _serviceProvider.LoadAllSharpExtensions();

        if (!PythonEngine.IsInitialized)
        {
            PythonEngine.Initialize();
            _threadState = PythonEngine.BeginAllowThreads();
        }

        using (Py.GIL())
        {
            dynamic sys = Py.Import("sys");
            string pythonSdkPath = Path.Combine(_sharpPath, "python");
            sys.path.append(pythonSdkPath);

            string pluginsPath = Path.Combine(_sharpPath, "python", "plugins");
            if (!Directory.Exists(pluginsPath))
            {
                Directory.CreateDirectory(pluginsPath);
            }
            sys.path.append(pluginsPath);

            LoadPlugins(pluginsPath);
        }

        return true;
    }

    public void HookEvent(string eventName, PyObject callback)
    {
        _gameEventManager.HookEvent(eventName, (IGameEvent ev, ref bool serverOnly) =>
        {
            using (Py.GIL())
            {
                try
                {
                    var info = new PythonGameEventInfo(serverOnly);
                    dynamic result = callback.Invoke(ev.ToPython(), info.ToPython());

                    serverOnly = info.DontBroadcast;

                    if (result != null)
                    {
                         try {
                             if (result is bool b && b == false)
                             {
                                return new HookReturnValue<bool>(EHookAction.SkipCallReturnOverride);
                             }
                             if (result.Equals(false))
                             {
                                 return new HookReturnValue<bool>(EHookAction.SkipCallReturnOverride);
                             }
                         } catch {}
                    }

                    return new HookReturnValue<bool>();
                }
                catch (Exception e)
                {
                    Console.WriteLine($"Python event error: {e}");
                    return new HookReturnValue<bool>();
                }
            }
        });
    }

    private void LoadPlugins(string pluginsPath)
    {
        var files = Directory.GetFiles(pluginsPath, "*.py", SearchOption.TopDirectoryOnly);
        foreach (var file in files)
        {
            try
            {
                string moduleName = Path.GetFileNameWithoutExtension(file);
                dynamic module = Py.Import(moduleName);

                var inspect = Py.Import("inspect");
                var sdk = Py.Import("ModSharp");
                var basePlugin = sdk.BasePlugin;

                dynamic members = inspect.getmembers(module, inspect.isclass);
                foreach (dynamic member in members)
                {
                    dynamic cls = member[1];
                    if (cls != basePlugin && PyObject.IsSubclass(cls, basePlugin))
                    {
                         LoadPluginInstance(cls, moduleName);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to load python plugin {file}: {ex}");
            }
        }
    }

    private void LoadPluginInstance(dynamic cls, string moduleName)
    {
        try
        {
            dynamic instance = cls(_sharedSystem, this);
            _loadedPlugins.Add(instance);

            instance.Load(false);
            RegisterCommands(instance);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error initializing plugin {moduleName}: {ex}");
        }
    }

    private void RegisterCommands(dynamic instance)
    {
        var inspect = Py.Import("inspect");
        dynamic methods = inspect.getmembers(instance, inspect.ismethod);

        foreach (dynamic methodPair in methods)
        {
            dynamic method = methodPair[1];
            if (PythonEngine.HasAttr(method, "_modsharp_console_command"))
            {
                dynamic info = method.GetAttr("_modsharp_console_command");
                string name = info.name;
                string desc = info.description;

                Func<IGameClient, StringCommand, ECommandAction> callback = (client, command) =>
                {
                    using (Py.GIL())
                    {
                        try
                        {
                            // Resolve IPlayerController if possible
                            // We use dynamic/object to pass to Python, handling types there or here
                            // User expects 'player' object with PrintToChat.
                            // IPlayerController has Print. IGameClient has ConsolePrint.

                            object playerObj = client;
                            if (client != null && client.IsValid)
                            {
                                var controller = _entityManager.FindPlayerControllerBySlot(client.Slot);
                                if (controller != null)
                                {
                                    playerObj = controller;
                                }
                            }

                            // Call python method: method(player, command)
                            method(playerObj.ToPython(), command.ToPython());

                            return ECommandAction.Stopped;
                        }
                        catch(Exception e)
                        {
                            Console.WriteLine($"Python command error: {e}");
                            return ECommandAction.Skipped;
                        }
                    }
                };

                _sharedSystem.GetClientManager().InstallCommandCallback(name, callback);
            }
        }
    }

    public void Shutdown()
    {
        _serviceProvider.ShutdownAllSharpExtensions();

        using (Py.GIL())
        {
            foreach (dynamic plugin in _loadedPlugins)
            {
                try { if (PythonEngine.HasAttr(plugin, "Unload")) plugin.Unload(); } catch { }
            }
            _loadedPlugins.Clear();
        }

        if (_threadState != IntPtr.Zero)
        {
            PythonEngine.EndAllowThreads(_threadState);
        }
        PythonEngine.Shutdown();
    }

    public string DisplayName => "Python Loader";
    public string DisplayAuthor => "Jules";
}
