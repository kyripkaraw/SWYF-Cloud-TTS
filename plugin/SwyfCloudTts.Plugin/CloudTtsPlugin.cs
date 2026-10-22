using System;
using System.Collections.Concurrent;
using System.Reflection;
using UnityEngine;
using UnityEngine.UIElements;

namespace SwyfCloudTts.Plugin
{
    /// <summary>
    /// Hosts the Cloud TTS card inside Settings > Speech. The game ships a stripped mscorlib
    /// (no MethodBase.GetMethodBody), so IL patching is impossible; instead the pause menu
    /// controller is polled and the card is attached once its speech screen exists.
    /// </summary>
    public sealed class CloudTtsPlugin : MonoBehaviour
    {
        private const float ScanInterval = 1f;

        private static readonly ConcurrentQueue<Action> Pending = new ConcurrentQueue<Action>();
        private static Type _controllerType;
        private static bool _controllerMissingLogged;
        private static bool _documentMissingLogged;
        private static bool _rootMissingLogged;
        private static bool _screenMissingLogged;
        private static bool _rebuild;

        private float _nextScan;
        private CloudTtsSection _section;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            PluginLog.Info("settings host created");
        }

        private void Update()
        {
            Action action;
            while (Pending.TryDequeue(out action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    PluginLog.Error("main-thread action failed: " + ex);
                }
            }

            if (Time.unscaledTime < _nextScan)
            {
                return;
            }

            _nextScan = Time.unscaledTime + ScanInterval;
            try
            {
                TryAttach();
            }
            catch (Exception ex)
            {
                PluginLog.Error("could not add the Cloud TTS section: " + ex);
            }
        }

        private void OnDestroy()
        {
            if (_section != null)
            {
                try
                {
                    _section.Dispose();
                }
                catch
                {
                    // The UI may already be torn down.
                }

                _section = null;
            }
        }

        private void TryAttach()
        {
            if (_section != null && _section.IsAttached && !_rebuild)
            {
                return;
            }

            if (_controllerType == null)
            {
                _controllerType = FindType("PauseMenuToolkitController");
                if (_controllerType == null)
                {
                    if (!_controllerMissingLogged)
                    {
                        _controllerMissingLogged = true;
                        PluginLog.Warning("PauseMenuToolkitController was not found. The game may have changed.");
                    }

                    return;
                }

                _controllerMissingLogged = false;
                PluginLog.Info("PauseMenuToolkitController resolved from " + _controllerType.Assembly.GetName().Name);
            }

            UnityEngine.Object controller = UnityEngine.Object.FindFirstObjectByType(_controllerType);
            if (controller == null)
            {
                return;
            }

            AttachSection(controller);
        }

        private void AttachSection(UnityEngine.Object controller)
        {
            if (_section != null)
            {
                if (_section.IsAttached && !_rebuild)
                {
                    return;
                }

                try
                {
                    _section.Dispose();
                }
                catch
                {
                    // The UI may already be torn down.
                }

                _section = null;
            }

            var behaviour = controller as MonoBehaviour;
            if (behaviour == null)
            {
                PluginLog.Warning("PauseMenuToolkitController is not a MonoBehaviour.");
                return;
            }

            UIDocument document = behaviour.GetComponent<UIDocument>();
            if (document == null)
            {
                document = behaviour.GetComponentInChildren<UIDocument>(true);
            }

            if (document == null)
            {
                if (!_documentMissingLogged)
                {
                    _documentMissingLogged = true;
                    PluginLog.Warning("PauseMenuToolkitController has no UIDocument yet.");
                }

                return;
            }

            VisualElement root = document.rootVisualElement;
            if (root == null)
            {
                if (!_rootMissingLogged)
                {
                    _rootMissingLogged = true;
                    PluginLog.Warning("The pause menu UIDocument has no root visual element yet.");
                }

                return;
            }

            VisualElement screen = root.Q<VisualElement>("speech-screen");
            if (screen == null)
            {
                if (!_screenMissingLogged)
                {
                    _screenMissingLogged = true;
                    PluginLog.Warning("speech-screen is missing; known elements: " + CollectNames(root, 40));
                }

                return;
            }

            VisualElement previous = screen.Q<VisualElement>("swyf-cloud-tts-section");
            if (previous != null)
            {
                if (previous.userData is CloudTtsSection oldSection)
                {
                    oldSection.Dispose();
                }

                previous.RemoveFromHierarchy();
            }

            _rebuild = false;
            CloudTtsTheme.Capture(root);
            CloudTtsConfig config = CloudTtsConfig.Load();
            _section = new CloudTtsSection(screen, root, config);
            _section.Attach();
            PluginLog.Info("Cloud TTS section added to Settings > Speech.");
        }

        internal static void Enqueue(Action action)
        {
            if (action != null)
            {
                Pending.Enqueue(action);
            }
        }

        /// <summary>Builds the card again on the next scan, for example in another language.</summary>
        internal static void RequestRebuild() => _rebuild = true;

        internal static bool ReloadWorker()
        {
            try
            {
                Type type = FindType("Kolkata.QwenTts.QwenTtsSingleton");
                if (type == null)
                {
                    PluginLog.Warning("QwenTtsSingleton was not found; the current worker will keep using the old settings.");
                    return false;
                }

                PropertyInfo instanceProperty = type.GetProperty(
                    "Instance", BindingFlags.Public | BindingFlags.Static);
                object instance = instanceProperty != null ? instanceProperty.GetValue(null, null) : null;
                if (instance == null)
                {
                    PluginLog.Info("the worker is not loaded yet; the new settings will be used on the next spoken line.");
                    return false;
                }

                MethodInfo unloadModel = type.GetMethod(
                    "UnloadModel", BindingFlags.Public | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                if (unloadModel == null)
                {
                    PluginLog.Warning("QwenTtsSingleton.UnloadModel() was not found.");
                    return false;
                }

                unloadModel.Invoke(instance, null);
                PluginLog.Info("worker unloaded; the next line will reload it with the saved settings.");
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Warning("could not reload the worker: " + ex.Message);
                return false;
            }
        }

        private static string CollectNames(VisualElement root, int limit)
        {
            var names = new System.Collections.Generic.List<string>();
            var pending = new System.Collections.Generic.List<VisualElement> { root };
            while (pending.Count > 0 && names.Count < limit)
            {
                VisualElement element = pending[pending.Count - 1];
                pending.RemoveAt(pending.Count - 1);
                if (!string.IsNullOrEmpty(element.name))
                {
                    names.Add(element.name);
                }

                foreach (VisualElement child in element.Children())
                {
                    pending.Add(child);
                }
            }

            return names.Count == 0 ? "(no named elements)" : string.Join(", ", names.ToArray());
        }

        internal static Type FindType(string fullName)
        {
            Type type = Type.GetType(fullName);
            if (type != null)
            {
                return type;
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    type = assembly.GetType(fullName, false, false);
                    if (type != null)
                    {
                        return type;
                    }
                }
                catch
                {
                    // Dynamic or partially loaded assemblies cannot be searched.
                }
            }

            return null;
        }
    }
}
