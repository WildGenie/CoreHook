﻿using System;
using System.IO;
using System.Reflection;
using CoreHook.BinaryInjection.ProcessUtils;
using CoreHook.BinaryInjection.ProcessUtils.Configuration;
using CoreHook.BinaryInjection.RemoteInjection;
using CoreHook.BinaryInjection.RemoteInjection.Configuration;
using CoreHook.FileMonitor.Service;
using CoreHook.IPC.Platform;
using CoreHook.Memory.Processes;

namespace CoreHook.FileMonitor
{
    class Program
    {
        /// <summary>
        /// The pipe name the FileMonitor RPC service communicates over between processes.
        /// </summary>
        private const string CoreHookPipeName = "CoreHook";

        /// <summary>
        /// The directory containing the CoreHook modules to be loaded in the target process.
        /// </summary>
        private const string HookLibraryDirName = "Hook";

        /// <summary>
        /// The library to be injected into the target process and executed
        /// using the EntryPoint's 'Run' Method.
        /// </summary>
        private const string HookLibraryName = "CoreHook.FileMonitor.Hook.dll";

        /// <summary>
        /// The name of the pipe used for notifying the host process
        /// if the hooking plugin has been loaded successfully in
        /// the target process or if loading failed.
        /// </summary>
        private const string InjectionPipeName = "CoreHookInjection";

        /// <summary>
        /// Enable verbose logging to the console for the CoreCLR hosting module.
        /// </summary>
        private const bool HostVerboseLog = false;

        /// <summary>
        /// Class that handles creating a named pipe server for communicating with the target process.
        /// </summary>
        private static readonly IPipePlatform PipePlatform = new PipePlatform();

        private static void Main(string[] args)
        {
            int targetProcessId = 0;
            string targetProgram = string.Empty;

            // Get the process to hook by file path for launching or process id for loading into.
            while ((args.Length != 1) || !ParseProcessId(args[0], out targetProcessId) || !FindOnPath(args[0]))
            {
                if (targetProcessId > 0)
                {
                    break;
                }
                if (args.Length != 1 || !FindOnPath(args[0]))
                {
                    Console.WriteLine();
                    Console.WriteLine("Usage: FileMonitor %PID%");
                    Console.WriteLine("   or: FileMonitor PathToExecutable");
                    Console.WriteLine();
                    Console.Write("Please enter a process Id or path to executable: ");

                    args = new string[]
                    {
                        Console.ReadLine()
                    };

                    if (string.IsNullOrEmpty(args[0]))
                    {
                        return;
                    }
                }
                else
                {
                    targetProgram = args[0];
                    break;
                }
            }

            var currentDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            string injectionLibrary = Path.Combine(currentDir, HookLibraryDirName, HookLibraryName);

            // Start process and begin dll loading.
            if (!string.IsNullOrWhiteSpace(targetProgram))
            {
                CreateAndInjectDll(targetProgram, injectionLibrary);
            }
            else
            {
                // Inject FileMonitor dll into process.
                InjectDllIntoTarget(targetProcessId, injectionLibrary);
            }

            // Start the RPC server for handling requests from the hooked program.
            StartListener();
        }

        /// <summary>
        /// Get an existing process ID by value or by name.
        /// </summary>
        /// <param name="targetProgram">The ID or name of a process to lookup.</param>
        /// <param name="processId">The ID of the process if found.</param>
        /// <returns>True if there is an existing process with the specified ID or name.</returns>
        private static bool ParseProcessId(string targetProgram, out int processId)
        {
            if (!int.TryParse(targetProgram, out processId))
            {
                var process = ProcessHelper.GetProcessByName(targetProgram);
                if (process != null)
                {
                    processId = process.Id;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Check if an application exists on the path.
        /// </summary>
        /// <param name="targetProgram">The program name, such as "notepad.exe".</param>
        /// <returns>True of the program is found on the path.</returns>
        private static bool FindOnPath(string targetProgram)
        {
            if (File.Exists(targetProgram))
            {
                return true;
            }

            var path = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrWhiteSpace(path))
            {
                foreach (var pathDir in path.Split(";"))
                {
                    var programPath = Path.Combine(pathDir, targetProgram);
                    if (File.Exists(programPath))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Check if a file path is valid, otherwise throw an exception.
        /// </summary>
        /// <param name="filePath">Path to a file or directory to validate.</param>
        private static void ValidateFilePath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException($"Invalid file path {filePath}");
            }
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"File path {filePath} does not exist");
            }
        }

        /// <summary>
        /// Start the application at <paramref name="exePath"/>
        /// and then inject and load the CoreHook hooking module <paramref name="injectionLibrary"/>
        /// in the newly created process.
        /// </summary>
        /// <param name="exePath">The path to the application to be launched.</param>
        /// <param name="injectionLibrary">The path of the plugin to be loaded in the target process.</param>
        /// <param name="injectionPipeName">The pipe name which receives messages during the plugin initialization stage.</param>
        private static void CreateAndInjectDll(
            string exePath,
            string injectionLibrary,
            string injectionPipeName = InjectionPipeName)
        {
            ValidateFilePath(injectionLibrary);

            if (Examples.Common.ModulesPathHelper.GetCoreLoadPaths(
                    false, out NativeModulesConfiguration config32) &&
                Examples.Common.ModulesPathHelper.GetCoreLoadPaths(
                    true, out NativeModulesConfiguration config64) &&
                Examples.Common.ModulesPathHelper.GetCoreLoadModulePath(
                    out string coreLoadLibrary))
            {
                RemoteInjector.CreateAndInject(
                     new ProcessCreationConfiguration
                     {
                         ExecutablePath = exePath,
                         CommandLine = null,
                         ProcessCreationFlags = 0x00
                     },
                     config32,
                     config64,
                     new RemoteInjectorConfiguration
                     {
                         ClrBootstrapLibrary = coreLoadLibrary,
                         InjectionPipeName = injectionPipeName,
                         PayloadLibrary = injectionLibrary,
                         VerboseLog = HostVerboseLog
                     },
                     PipePlatform,
                     out _,
                     CoreHookPipeName);
            }
        }

        /// <summary>
        /// Inject and load the CoreHook hooking module <paramref name="injectionLibrary"/>
        /// in the existing created process referenced by <paramref name="processId"/>.
        /// </summary>
        /// <param name="processId">The target process ID to inject and load plugin into.</param>
        /// <param name="injectionLibrary">The path of the plugin that is loaded into the target process.</param>
        /// <param name="injectionPipeName">The pipe name which receives messages during the plugin initialization stage.</param>
        private static void InjectDllIntoTarget(
            int processId,
            string injectionLibrary,
            string injectionPipeName = InjectionPipeName)
        {
            ValidateFilePath(injectionLibrary);

            if (Examples.Common.ModulesPathHelper.GetCoreLoadPaths(
                    ProcessHelper.GetProcessById(processId).Is64Bit(),
                    out NativeModulesConfiguration nativeConfig) &&
                Examples.Common.ModulesPathHelper.GetCoreLoadModulePath(
                    out string coreLoadLibrary))
            {
                RemoteInjector.Inject(
                    processId,
                    new RemoteInjectorConfiguration(nativeConfig)
                    {
                        InjectionPipeName = injectionPipeName,
                        ClrBootstrapLibrary = coreLoadLibrary,
                        PayloadLibrary = injectionLibrary,
                        VerboseLog = HostVerboseLog
                    },
                    PipePlatform,
                    CoreHookPipeName);
            }
        }

        /// <summary>
        /// Create an RPC server that is called by the RPC client started in
        /// a target process.
        /// </summary>
        private static void StartListener()
        {
            var session = new FileMonitorSessionFeature();

            Examples.Common.RpcService.CreateRpcService(
                  CoreHookPipeName,
                  PipePlatform,
                  session,
                  typeof(FileMonitorService),
                  async (context, next) =>
                  {
                      Console.WriteLine("> {0}", context.Request);
                      await next();
                      Console.WriteLine("< {0}", context.Response);
                  });

            Console.WriteLine("Press Enter to quit.");
            Console.ReadLine();

            session.StopServer();
        }
    }
}