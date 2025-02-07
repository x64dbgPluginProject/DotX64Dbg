using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Dotx64Dbg
{
    internal class PluginInfo
    {
        public string Name
        {
            get; set;
        }
        public string Description
        {
            get; set;
        }
        public string Version
        {
            get; set;
        }
        public string Author
        {
            get; set;
        }
        public string Website
        {
            get; set;
        }

        public string[] Dependencies
        {
            get; set;
        }
    }

    internal class Plugin
    {
        public PluginInfo Info;
        public string Path;
        public string ConfigPath;
        public string BuildOutputPath;
        public List<string> SourceFiles;
        public bool RequiresRebuild;
        internal AssemblyLoader Loader;
        internal string AssemblyPath;
        internal object Instance;
        internal Type InstanceType;

        public string ProjectFilePath
        {
            get
            {
                if (Info == null)
                    return null;
                return System.IO.Path.Combine(Path, Info.Name + ".csproj");
            }
        }
    }

    internal partial class Plugins
    {
        string PluginsPath = "dotplugins";
        string AppDataPath;
        string PluginOutputPath;

        DependencyResolver dependencyResolver;

        List<Plugin> Registered = new();

        private void SetupDirectories()
        {
            PluginsPath = Settings.PluginsPath;
            if (!Directory.Exists(PluginsPath))
            {
                try
                {
                    Directory.CreateDirectory(PluginsPath);
                }
                catch (Exception)
                {
                    Console.WriteLine("Unable to create directory for plugins: {0}", PluginsPath);
                }
            }

            Console.WriteLine("DotX64Dbg Plugins Path: {0}", PluginsPath);

            AppDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DotX64Dbg");
#if _X64_
            PluginOutputPath = Path.Combine(AppDataPath, "x64");
#else
            PluginOutputPath = Path.Combine(AppDataPath, "x86");
#endif
            PluginOutputPath = Path.Combine(PluginOutputPath, "Plugins");
            if (!Directory.Exists(PluginOutputPath))
            {
                Directory.CreateDirectory(PluginOutputPath);
            }
        }

        public void Initialize()
        {
            SetupDirectories();
            InitializeWatcher();

            dependencyResolver = new();
            dependencyResolver.AddResolver(new NuGetDependencyResolver());
            dependencyResolver.AddResolver(new LocalAssembliesResolver());

            SkipRebuilding = true;
            RegisterPlugins();
            GenerateProjects();
            StartBuildWorker();

            SkipRebuilding = false;
            TriggerRebuild();
        }

        public void Shutdown()
        {
            StopBuildWorker();
        }

        void RegisterPlugins()
        {
            var dirs = new List<string>(Directory.EnumerateDirectories(PluginsPath));
            foreach (var dir in dirs)
            {
                RegisterPlugin(dir);
            }
        }

        void GenerateProject(Plugin plugin)
        {
            var binaryPathX86 = Path.Combine(Utils.GetRootPath(), "x86", "plugins");
            var binaryPathX64 = Path.Combine(Utils.GetRootPath(), "x64", "plugins");
            var assemblies = new string[] {
                "Dotx64Dbg.Bindings.dll", "Dotx64Dbg.Managed.dll"
            };

            if (plugin.Info == null)
                return;

            var projectFilePath = plugin.ProjectFilePath;
            Console.WriteLine($"Generating project file for {plugin.Info.Name}");

            var projGen = new ProjectGenerator();
            projGen.ReferencePathX86 = binaryPathX86;
            projGen.ReferencePathX64 = binaryPathX64;
            projGen.References = assemblies;
            if (plugin.Info.Dependencies is not null)
            {
                projGen.Frameworks = plugin.Info.Dependencies
                    .Where(deps => NuGetDependencyResolver.VersioningHelper.IsValidDotNetFrameworkName(deps))
                    .Select(deps => new NuGet.Frameworks.NuGetFramework(
                            NuGetDependencyResolver.VersioningHelper.GetFrameworkName(deps),
                            new Version(NuGetDependencyResolver.VersioningHelper.GetFrameworkVersion(deps)))
                    ).ToArray();
            }

            projGen.Save(projectFilePath);
        }

        void GenerateProjects()
        {
            foreach (var plugin in Registered)
            {
                GenerateProject(plugin);
            }
        }

        List<string> EnumerateSourceFiles(string pluginPath)
        {
            return Directory.EnumerateFiles(pluginPath, "*.cs", new EnumerationOptions()
            {
                RecurseSubdirectories = true,
            }).Where(file => !IsExcludedFileOrFolder(pluginPath, file)).ToList();
        }

        PluginInfo GetPluginInfo(string jsonFile)
        {
            try
            {
                var jsonString = Utils.ReadFileContents(jsonFile);
                var pluginInfo = JsonSerializer.Deserialize<PluginInfo>(jsonString);

                var res = JsonSerializer.Deserialize<PluginInfo>(jsonString);
                if (res.Dependencies == null)
                {
                    // Ensure this is never null.
                    res.Dependencies = Array.Empty<string>();
                }

                // Ensure no duplicates exist.
                res.Dependencies = res.Dependencies.Distinct().ToArray();

                // This list has to be sorted to avoid rebuilds when only the order changes.
                Array.Sort(res.Dependencies);

                return res;
            }
            catch (System.Exception)
            {
                return null;
            }
        }

        void RegisterPlugin(string path)
        {
            var jsonFile = Path.Combine(path, "plugin.json");
            var pluginInfo = GetPluginInfo(jsonFile);
            var pathName = Path.GetFileName(path);

            var plugin = new Plugin()
            {
                Info = pluginInfo,
                Path = path,
                ConfigPath = jsonFile,
                RequiresRebuild = false,
                BuildOutputPath = Path.Combine(PluginOutputPath, pathName),
                SourceFiles = EnumerateSourceFiles(path),
            };

            if (!Directory.Exists(plugin.BuildOutputPath))
            {
                Directory.CreateDirectory(plugin.BuildOutputPath);
            }

            Registered.Add(plugin);
            Utils.DebugPrintLine($"Registered new plugin: {plugin.Path}");

            if (plugin.Info != null)
            {
                LoadPlugin(plugin);
            }
        }

        void RemovePlugin(Plugin plugin)
        {
            Utils.DebugPrintLine($"Removing plugin: {plugin.Path}");

            UnloadPlugin(plugin);

            for (var i = 0; i < Registered.Count; ++i)
            {
                if (Registered[i].Path == plugin.Path)
                {
                    Registered.RemoveAt(i);
                    break;
                }
            }
        }

        void LoadPlugin(Plugin plugin)
        {
            var pluginInfo = GetPluginInfo(plugin.ConfigPath);
            if (pluginInfo == null)
            {
                Utils.DebugPrintLine("Unable to load plugin info.");
                return;
            }

            Utils.DebugPrintLine("Plugin meta loaded, activating plugin.");
            plugin.Info = pluginInfo;

            if (!File.Exists(plugin.ProjectFilePath))
            {
                GenerateProject(plugin);
            }

            plugin.RequiresRebuild = true;
            TriggerRebuild();
        }

        Plugin FindPlugin(string path)
        {
            foreach (var plugin in Registered)
            {
                if (plugin.Path == path)
                {
                    return plugin;
                }
            }
            return null;
        }

        void RebuildOrUnloadPlugin(Plugin plugin)
        {
            if (plugin.SourceFiles.Count == 0)
            {
                Utils.DebugPrintLine($"[PluginWatch] Plugin {plugin.Info.Name} has no sources, unloading.");
                UnloadPlugin(plugin);
            }
            else
            {
                plugin.RequiresRebuild = true;
                TriggerRebuild();
            }
        }

        bool CheckDependeniesChanged(string[] left, string[] right)
        {
            if (left.Length != right.Length)
                return true;

            if (Enumerable.SequenceEqual(left, right))
                return false;

            return true;
        }

        public List<IPlugin> GetPluginInstances()
        {
            // If we are currently rebuilding we have to wait.
            WaitForRebuild();

            return Registered
                .Select(x => x.Instance as IPlugin)
                .Where(x => x != null)
                .ToList();
        }

        internal bool IsPluginNameTaken(string pluginName)
        {
            var path = Path.Combine(Settings.PluginsPath, pluginName);
            if (Directory.Exists(path))
            {
                return true;
            }
            return false;
        }


        public string CreatePluginTemplate(string pluginName)
        {
            var pluginPath = Path.Combine(Settings.PluginsPath, pluginName);
            if (Directory.Exists(pluginPath))
            {
                return null;
            }

            var pluginJsonPath = Path.Combine(pluginPath, "plugin.json");
            var pluginCsPath = Path.Combine(pluginPath, "plugin.cs");

            // Search and replace keywords in templates.
            var replacements = new Dictionary<string, string> {
                { "%PLUGIN_NAME%", pluginName }
            };

            if (!Utils.CreateDir(pluginPath))
            {
                // ERROR.
                return null;
            }

            if (!Utils.WriteReplacedContents(Dotx64Dbg.Properties.Resources.plugin_json, replacements, pluginJsonPath))
            {
                // ERROR.
            }

            if (!Utils.WriteReplacedContents(Dotx64Dbg.Properties.Resources.plugin_cs, replacements, pluginCsPath))
            {
                // ERROR.
            }

            return pluginPath;
        }
    }
}
