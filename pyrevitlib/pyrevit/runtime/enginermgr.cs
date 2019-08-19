using System;
using System.IO;
using System.Collections.Generic;

using Microsoft.Scripting.Hosting;
using IronPython.Hosting;


namespace PyRevitLabs.PyRevit.Runtime {
    public class IronPythonEngineManager
    {
        private List<string> _commandBuiltins = new List<string>();

        public IronPythonEngineManager() {}

        public ScriptEngine GetEngine(ref ScriptRuntime pyrvtScript)
        {
            ScriptEngine engine;
            bool cachedEngine = false;

            // If the command required a fullframe engine
            if (pyrvtScript.NeedsFullFrameEngine)
                engine = CreateNewEngine(ref pyrvtScript, fullframe: true);

            // If the command required a clean engine
            else if (pyrvtScript.NeedsCleanEngine)
                engine = CreateNewEngine(ref pyrvtScript);

            // if the user is asking to refresh the cached engine for the command,
            // then update the engine and save in cache
            else if (pyrvtScript.NeedsRefreshedEngine)
                engine = RefreshCachedEngine(ref pyrvtScript);

            // if not above, get/create cached engine
            else {
                engine = GetCachedEngine(ref pyrvtScript);
                cachedEngine = true;
            }

            // now that the engine is ready, setup the builtins and io streams
            SetupStreams(engine, pyrvtScript.OutputStream);
            SetupBuiltins(engine, ref pyrvtScript, cachedEngine);
            SetupSearchPaths(engine, pyrvtScript.ModuleSearchPaths);
            SetupArguments(engine, pyrvtScript.Arguments);

            return engine;
        }

        public Dictionary<string, ScriptEngine> EngineDict
        {
            get
            {
                var engineDict = (Dictionary<string, ScriptEngine>) AppDomain.CurrentDomain.GetData(DomainStorageKeys.IronPythonEnginesDictKey);

                if (engineDict == null)
                    engineDict = ClearEngines();

                return engineDict;
            }
        }

        public Tuple<Stream, System.Text.Encoding> DefaultOutputStreamConfig
        {
            get
            {
                return (Tuple<Stream, System.Text.Encoding>)AppDomain.CurrentDomain.GetData(DomainStorageKeys.IronPythonEngineDefaultStreamCfgKey);
            }

            set
            {
                AppDomain.CurrentDomain.SetData(DomainStorageKeys.IronPythonEngineDefaultStreamCfgKey, value);
            }
        }

        public Dictionary<string, ScriptEngine> ClearEngines()
        {
            var newEngineDict = new Dictionary<string, ScriptEngine>();
            AppDomain.CurrentDomain.SetData(DomainStorageKeys.IronPythonEnginesDictKey, newEngineDict);

            return newEngineDict;
        }

        public void CleanupEngine(ScriptEngine engine)
        {
            CleanupEngineBuiltins(engine);
            CleanupStreams(engine);
        }

        private ScriptEngine CreateNewEngine(ref ScriptRuntime pyrvtScript, bool fullframe=false)
        {
            var flags = new Dictionary<string, object>();

            // default flags
            flags["LightweightScopes"] = true;

            if (fullframe)
            {
                flags["Frames"] = true;
                flags["FullFrames"] = true;
            }

            var engine = IronPython.Hosting.Python.CreateEngine(flags);

            // also, allow access to the PyRevitLoader internals
            engine.Runtime.LoadAssembly(typeof(PyRevitLoader.ScriptExecutor).Assembly);

            // also, allow access to the PyRevitRuntime internals
            engine.Runtime.LoadAssembly(typeof(ScriptExecutor).Assembly);

            // reference RevitAPI and RevitAPIUI
            engine.Runtime.LoadAssembly(typeof(Autodesk.Revit.DB.Document).Assembly);
            engine.Runtime.LoadAssembly(typeof(Autodesk.Revit.UI.TaskDialog).Assembly);

            // save the default stream for later resetting the streams
            DefaultOutputStreamConfig = new Tuple<Stream, System.Text.Encoding>(engine.Runtime.IO.OutputStream, engine.Runtime.IO.OutputEncoding);

            // setup stdlib
            SetupStdlib(engine);

            return engine;
        }

        private ScriptEngine CreateNewCachedEngine(ref ScriptRuntime pyrvtScript)
        {
            var newEngine = CreateNewEngine(ref pyrvtScript);
            this.EngineDict[pyrvtScript.ScriptData.CommandExtension] = newEngine;
            return newEngine;
        }

        private ScriptEngine GetCachedEngine(ref ScriptRuntime pyrvtScript)
        {
            if (this.EngineDict.ContainsKey(pyrvtScript.ScriptData.CommandExtension))
            {
                var existingEngine = this.EngineDict[pyrvtScript.ScriptData.CommandExtension];
                return existingEngine;
            }
            else
            {
                return CreateNewCachedEngine(ref pyrvtScript);
            }
        }

        private ScriptEngine RefreshCachedEngine(ref ScriptRuntime pyrvtScript)
        {
            return CreateNewCachedEngine(ref pyrvtScript);
        }

        private void SetupStdlib(ScriptEngine engine)
        {
            // ask PyRevitLoader to add it's resource ZIP file that contains the IronPython
            // standard library to this engine
            var tempExec = new PyRevitLoader.ScriptExecutor();
            tempExec.AddEmbeddedLib(engine);
        }

        private void SetupSearchPaths(ScriptEngine engine, List<string> searchPaths)
        {
            // process search paths provided to executor
            engine.SetSearchPaths(searchPaths);
        }

        private void SetupArguments(ScriptEngine engine, List<string> arguments)
        {
            // setup arguments (sets sys.argv)
            // engine.Setup.Options["Arguments"] = arguments;
            // engine.Runtime.Setup.HostArguments = new List<object>(arguments);
            var sysmodule = engine.GetSysModule();
            var pythonArgv = new IronPython.Runtime.List();
            pythonArgv.extend(arguments);
            sysmodule.SetVariable("argv", pythonArgv);
        }

        private void SetupBuiltins(ScriptEngine engine, ref ScriptRuntime pyrvtScript, bool cachedEngine)
        {
            // BUILTINS -----------------------------------------------------------------------------------------------
            // Get builtin to add custom variables
            var builtin = IronPython.Hosting.Python.GetBuiltinModule(engine);

            // Let commands know if they're being run in a cached engine
            builtin.SetVariable("__cachedengine__", cachedEngine);

            // Add current engine manager to builtins
            builtin.SetVariable("__ipyenginemanager__", this);

            // Add this script executor to the the builtin to be globally visible everywhere
            // This support pyrevit functionality to ask information about the current executing command
            builtin.SetVariable("__externalcommand__", pyrvtScript);

            // Add host application handle to the builtin to be globally visible everywhere
            if (pyrvtScript.UIApp != null)
                builtin.SetVariable("__revit__", pyrvtScript.UIApp);
            else if (pyrvtScript.UIControlledApp != null)
                builtin.SetVariable("__revit__", pyrvtScript.UIControlledApp);
            else if (pyrvtScript.App != null)
                builtin.SetVariable("__revit__", pyrvtScript.App);
            else
                builtin.SetVariable("__revit__", null);

            // Adding data provided by IExternalCommand.Execute
            builtin.SetVariable("__commanddata__", pyrvtScript.CommandData);
            builtin.SetVariable("__elements__", pyrvtScript.SelectedElements);

            // Adding information on the command being executed
            builtin.SetVariable("__commandpath__", Path.GetDirectoryName(pyrvtScript.ScriptData.ScriptPath));
            builtin.SetVariable("__configcommandpath__", Path.GetDirectoryName(pyrvtScript.ScriptData.ConfigScriptPath));
            builtin.SetVariable("__commandname__", pyrvtScript.ScriptData.CommandName);
            builtin.SetVariable("__commandbundle__", pyrvtScript.ScriptData.CommandBundle);
            builtin.SetVariable("__commandextension__", pyrvtScript.ScriptData.CommandExtension);
            builtin.SetVariable("__commanduniqueid__", pyrvtScript.ScriptData.CommandUniqueId);
            builtin.SetVariable("__forceddebugmode__", pyrvtScript.DebugMode);
            builtin.SetVariable("__shiftclick__", pyrvtScript.ConfigMode);

            // Add reference to the results dictionary
            // so the command can add custom values for logging
            builtin.SetVariable("__result__", pyrvtScript.GetResultsDictionary());

            // EVENT HOOKS BUILTINS ----------------------------------------------------------------------------------
            // set event arguments for engine
            builtin.SetVariable("__eventsender__", pyrvtScript.EventSender);
            builtin.SetVariable("__eventargs__", pyrvtScript.EventArgs);


            // CUSTOM BUILTINS ---------------------------------------------------------------------------------------
            var commandBuiltins = pyrvtScript.GetBuiltInVariables();
            if (commandBuiltins != null)
                foreach (KeyValuePair<string, object> data in commandBuiltins) {
                    _commandBuiltins.Add(data.Key);
                    builtin.SetVariable(data.Key, (object)data.Value);
                }
        }

        private void SetupStreams(ScriptEngine engine, ScriptOutputStream outStream)
        {
            engine.Runtime.IO.SetOutput(outStream, System.Text.Encoding.UTF8);
        }

        private void CleanupEngineBuiltins(ScriptEngine engine)
        {
            var builtin = IronPython.Hosting.Python.GetBuiltinModule(engine);

            builtin.SetVariable("__cachedengine__",         (Object)null);
            builtin.SetVariable("__ipyenginemanager__",     (Object)null);
            builtin.SetVariable("__externalcommand__",      (Object)null);
            builtin.SetVariable("__commanddata__",          (Object)null);
            builtin.SetVariable("__elements__",             (Object)null);
            builtin.SetVariable("__commandpath__",          (Object)null);
            builtin.SetVariable("__configcommandpath__",    (Object)null);
            builtin.SetVariable("__commandname__",          (Object)null);
            builtin.SetVariable("__commandbundle__",        (Object)null);
            builtin.SetVariable("__commandextension__",     (Object)null);
            builtin.SetVariable("__commanduniqueid__",      (Object)null);
            builtin.SetVariable("__forceddebugmode__",      (Object)null);
            builtin.SetVariable("__shiftclick__",           (Object)null);

            builtin.SetVariable("__result__",               (Object)null);

            builtin.SetVariable("__eventsender__",          (Object)null);
            builtin.SetVariable("__eventargs__",            (Object)null);

            // cleanup all data set by command custom builtins
            if (_commandBuiltins.Count > 0)
                foreach(string builtinVarName in _commandBuiltins)
                    builtin.SetVariable(builtinVarName, (Object)null);
        }

        private void CleanupStreams(ScriptEngine engine)
        {
            // Remove IO streams references so GC can collect
            Tuple<Stream, System.Text.Encoding> outStream = this.DefaultOutputStreamConfig;
            if (outStream != null)
            {
                engine.Runtime.IO.SetOutput(outStream.Item1, outStream.Item2);
                outStream.Item1.Dispose();
            }
        }
    }
}