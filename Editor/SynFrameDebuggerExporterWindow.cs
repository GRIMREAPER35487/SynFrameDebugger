using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Synthos.SynSceneOptimizer
{
    public class SynFrameDebuggerExporterWindow : EditorWindow
    {
        [Serializable]
        public class CapturedEvent
        {
            public int index;
            public string name;
            public int level;
            public string type = "Unknown Type";
            public string shaderName = "";
            public string materialName = "";
            public int vertexCount = 0;
            public string batchBreakCause = "None";
            public Dictionary<string, string> eventFields = new Dictionary<string, string>();
            public Dictionary<string, string> eventDataFields = new Dictionary<string, string>();
        }

        // Reflection cache
        private static Type frameDebuggerUtilityType;
        private static PropertyInfo enabledProp;
        private static PropertyInfo countProp;
        private static PropertyInfo limitProp;
        private static MethodInfo getFrameEventsMethod;
        private static MethodInfo getFrameEventDataMethod;
        private static Type frameDebuggerEventDataType;
        private static string[] batchBreakCauses;

        // UI State
        private List<CapturedEvent> capturedEvents = new List<CapturedEvent>();
        private Vector2 listScrollPos = Vector2.zero;
        private Vector2 detailsScrollPos = Vector2.zero;
        private CapturedEvent selectedEvent = null;
        private string searchFilter = "";
        private bool isReflectionResolved = false;
        private string reflectionStatus = "Not Init";

        // Stats
        private int totalDrawCalls = 0;
        private int totalVertices = 0;
        private HashSet<string> uniqueShaders = new HashSet<string>();

        // Async Capture State
        private bool isCapturingAsync = false;
        private int asyncCurrentIndex = 0;
        private int asyncTotalEvents = 0;
        private List<CapturedEvent> asyncCapturedEvents = new List<CapturedEvent>();
        private int originalLimit = 0;
        private object frameDebuggerWindowInstance = null;
        private MethodInfo changeLimitMethodInfo = null;
        private int asyncFrameWaitCount = 0;

        [MenuItem("Window/Synthos/Syn Frame Exporter", priority = 30)]
        [MenuItem("Tools/Synthos/Syn Frame Exporter", priority = 30)]
        public static void ShowWindow()
        {
            var window = GetWindow<SynFrameDebuggerExporterWindow>("Syn Frame Exporter");
            window.minSize = new Vector2(850, 500);
            window.Show();
        }

        private void OnEnable()
        {
            isReflectionResolved = ResolveReflection();
        }

        private bool ResolveReflection()
        {
            try
            {
                var assembly = typeof(EditorWindow).Assembly;
                
                // Unity 2022+ uses UnityEditorInternal.FrameDebuggerInternal namespace
                frameDebuggerUtilityType = assembly.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerUtility") ??
                                           assembly.GetType("UnityEditorInternal.FrameDebuggerUtility");
                                           
                if (frameDebuggerUtilityType == null)
                {
                    reflectionStatus = "Could not find FrameDebuggerUtility type.";
                    return false;
                }

                var unityEngineAssembly = typeof(UnityEngine.FrameDebugger).Assembly;
                Type unityEngineFrameDebuggerType = unityEngineAssembly.GetType("UnityEngine.FrameDebugger");
                if (unityEngineFrameDebuggerType != null)
                {
                    enabledProp = unityEngineFrameDebuggerType.GetProperty("enabled", BindingFlags.Public | BindingFlags.Static);
                }

                countProp = frameDebuggerUtilityType.GetProperty("count", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                limitProp = frameDebuggerUtilityType.GetProperty("limit", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                getFrameEventsMethod = frameDebuggerUtilityType.GetMethod("GetFrameEvents", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                getFrameEventDataMethod = frameDebuggerUtilityType.GetMethod("GetFrameEventData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                frameDebuggerEventDataType = assembly.GetType("UnityEditorInternal.FrameDebuggerInternal.FrameDebuggerEventData") ??
                                             assembly.GetType("UnityEditorInternal.FrameDebuggerEventData");

                var missingList = new List<string>();
                if (enabledProp == null) missingList.Add("enabledProp (on UnityEngine.FrameDebugger)");
                if (countProp == null) missingList.Add("countProp");
                if (limitProp == null) missingList.Add("limitProp");
                if (getFrameEventsMethod == null) missingList.Add("getFrameEventsMethod");
                if (getFrameEventDataMethod == null) missingList.Add("getFrameEventDataMethod");
                if (frameDebuggerEventDataType == null) missingList.Add("frameDebuggerEventDataType");

                if (missingList.Count > 0)
                {
                    reflectionStatus = "Missing: " + string.Join(", ", missingList);
                    return false;
                }

                var getBatchBreakCauseStringsMethod = frameDebuggerUtilityType.GetMethod("GetBatchBreakCauseStrings", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (getBatchBreakCauseStringsMethod != null)
                {
                    batchBreakCauses = (string[])getBatchBreakCauseStringsMethod.Invoke(null, null);
                }

                reflectionStatus = "Successfully resolved reflection APIs";
                return true;
            }
            catch (Exception e)
            {
                reflectionStatus = $"Reflection failed: {e.Message}";
                return false;
            }
        }

        private bool IsDebuggerEnabled()
        {
            if (!isReflectionResolved) return false;
            return (bool)enabledProp.GetValue(null);
        }

        private void SetDebuggerEnabled(bool enabled)
        {
            if (!isReflectionResolved) return;
            var setEnabledMethod = frameDebuggerUtilityType.GetMethod("SetEnabled", BindingFlags.Public | BindingFlags.Static);
            if (setEnabledMethod != null)
            {
                setEnabledMethod.Invoke(null, new object[] { enabled, 0 });
            }
            else
            {
                var p = frameDebuggerUtilityType.GetProperty("enabled", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (p != null && p.CanWrite)
                {
                    p.SetValue(null, enabled);
                }
            }
        }

        private int GetEventCount()
        {
            if (!isReflectionResolved) return 0;
            return (int)countProp.GetValue(null);
        }

        private void OnGUI()
        {
            if (isCapturingAsync)
            {
                GUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
                GUILayout.FlexibleSpace();
                
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.Width(450));
                
                GUILayout.Space(10);
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                GUILayout.Label("Capturing Graphics Event Details...", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(15);
                
                GUILayout.Label($"Processing event {asyncCurrentIndex} of {asyncTotalEvents}...");
                GUILayout.Space(5);
                
                Rect r = EditorGUILayout.GetControlRect(false, 22);
                EditorGUI.ProgressBar(r, (float)asyncCurrentIndex / asyncTotalEvents, $"{(int)(((float)asyncCurrentIndex / asyncTotalEvents) * 100)}%");
                
                GUILayout.Space(15);
                GUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Cancel Capture", GUILayout.Width(150), GUILayout.Height(25)))
                {
                    isCapturingAsync = false;
                    EditorApplication.update -= UpdateAsyncCapture;
                    if (limitProp != null)
                    {
                        limitProp.SetValue(null, originalLimit);
                    }
                    Debug.LogWarning("[SYN FRAME EXPORTER] Capture canceled by user.");
                }
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                GUILayout.Space(10);
                
                GUILayout.EndVertical();
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();
                
                GUILayout.FlexibleSpace();
                GUILayout.EndVertical();
                return;
            }

            // Title Header with Premium Aesthetics styling
            EditorGUILayout.Space();
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            GUILayout.Label("SYN FRAME DEBUGGER EXPORTER", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();

            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

            if (!isReflectionResolved)
            {
                EditorGUILayout.HelpBox($"Reflection failed to initialize. The exporter cannot run on this Unity version.\nDetails: {reflectionStatus}", MessageType.Error);
                if (GUILayout.Button("Retry Reflection Resolution"))
                {
                    isReflectionResolved = ResolveReflection();
                }
                return;
            }

            // Top Status Panel
            DrawStatusPanel();

            EditorGUILayout.Space(5);

            // Statistics dashboard (only shown if data is captured)
            if (capturedEvents.Count > 0)
            {
                DrawStatsDashboard();
                EditorGUILayout.Space(5);
            }

            // Search Filter and Export buttons
            DrawControlToolbar();

            EditorGUILayout.Space(5);

            // Main Split Panel: Left is Event List, Right is Details Panel
            GUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
            
            // Left Panel (Event List)
            GUILayout.BeginVertical(GUILayout.Width(position.width * 0.65f), GUILayout.ExpandHeight(true));
            DrawEventList();
            GUILayout.EndVertical();

            // Divider Line
            GUILayout.Box("", GUILayout.Width(2), GUILayout.ExpandHeight(true));

            // Right Panel (Details)
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            DrawEventDetailsPanel();
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
        }

        private void DrawStatusPanel()
        {
            GUILayout.BeginVertical(EditorStyles.helpBox);
            GUILayout.BeginHorizontal();

            bool isEnabled = IsDebuggerEnabled();
            string statusStr = isEnabled ? "ENABLED" : "DISABLED";
            Color statusColor = isEnabled ? new Color(0.1f, 0.6f, 0.1f) : new Color(0.8f, 0.2f, 0.2f);

            GUIStyle statusStyle = new GUIStyle(EditorStyles.boldLabel);
            statusStyle.normal.textColor = statusColor;

            GUILayout.Label("Frame Debugger Status: ", EditorStyles.boldLabel);
            GUILayout.Label(statusStr, statusStyle);
            GUILayout.FlexibleSpace();

            if (isEnabled)
            {
                GUI.backgroundColor = new Color(0.9f, 0.3f, 0.3f);
                if (GUILayout.Button("Disable Debugger", GUILayout.Width(130), GUILayout.Height(22)))
                {
                    SetDebuggerEnabled(false);
                }
            }
            else
            {
                GUI.backgroundColor = new Color(0.3f, 0.6f, 0.9f);
                if (GUILayout.Button("Enable Debugger", GUILayout.Width(130), GUILayout.Height(22)))
                {
                    SetDebuggerEnabled(true);
                }
            }
            GUI.backgroundColor = Color.white;

            EditorGUI.BeginDisabledGroup(!isEnabled);
            GUI.backgroundColor = new Color(0.3f, 0.8f, 0.4f);
            if (GUILayout.Button("Capture Frame", GUILayout.Width(120), GUILayout.Height(22)))
            {
                CaptureFrameData();
            }
            GUI.backgroundColor = Color.white;
            EditorGUI.EndDisabledGroup();

            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        private void DrawStatsDashboard()
        {
            GUILayout.BeginHorizontal(EditorStyles.helpBox);
            
            // Col 1
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            GUILayout.Label("Captured Events", EditorStyles.miniLabel);
            GUILayout.Label(capturedEvents.Count.ToString(), EditorStyles.boldLabel);
            GUILayout.EndVertical();

            // Col 2
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            GUILayout.Label("Draw Calls", EditorStyles.miniLabel);
            GUILayout.Label(totalDrawCalls.ToString(), EditorStyles.boldLabel);
            GUILayout.EndVertical();

            // Col 3
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            GUILayout.Label("Total Vertices", EditorStyles.miniLabel);
            GUILayout.Label(totalVertices.ToString("N0"), EditorStyles.boldLabel);
            GUILayout.EndVertical();

            // Col 4
            GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
            GUILayout.Label("Unique Shaders", EditorStyles.miniLabel);
            GUILayout.Label(uniqueShaders.Count.ToString(), EditorStyles.boldLabel);
            GUILayout.EndVertical();

            GUILayout.EndHorizontal();
        }

        private void DrawControlToolbar()
        {
            GUILayout.BeginHorizontal();

            // Search field
            GUILayout.Label("Filter: ", GUILayout.Width(45));
            searchFilter = EditorGUILayout.TextField(searchFilter, GUILayout.Height(20));

            if (GUILayout.Button("Clear", GUILayout.Width(50), GUILayout.Height(20)))
            {
                searchFilter = "";
                GUI.FocusControl(null);
            }

            GUILayout.Space(20);

            // Export Buttons
            EditorGUI.BeginDisabledGroup(capturedEvents.Count == 0);
            if (GUILayout.Button("Export CSV", GUILayout.Width(90), GUILayout.Height(20)))
            {
                ExportCSV();
            }
            if (GUILayout.Button("Export JSON", GUILayout.Width(90), GUILayout.Height(20)))
            {
                ExportJSON();
            }
            if (GUILayout.Button("Export TXT", GUILayout.Width(90), GUILayout.Height(20)))
            {
                ExportTXT();
            }
            EditorGUI.EndDisabledGroup();

            GUILayout.EndHorizontal();
        }

        private void DrawEventList()
        {
            GUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.ExpandHeight(true));
            
            // Header
            GUILayout.BeginHorizontal();
            GUILayout.Label("Idx", EditorStyles.boldLabel, GUILayout.Width(35));
            GUILayout.Label("Rendering Event Hierarchy / Name", EditorStyles.boldLabel);
            GUILayout.Label("Vertices", EditorStyles.boldLabel, GUILayout.Width(65));
            GUILayout.Label("Batch Break Cause", EditorStyles.boldLabel, GUILayout.Width(150));
            GUILayout.Label("Shader", EditorStyles.boldLabel, GUILayout.Width(100));
            GUILayout.EndHorizontal();
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider, GUILayout.Height(6));

            listScrollPos = EditorGUILayout.BeginScrollView(listScrollPos);

            int displayedCount = 0;
            string filterLower = searchFilter.ToLower();

            foreach (var evt in capturedEvents)
            {
                // Apply search filter
                if (!string.IsNullOrEmpty(searchFilter))
                {
                    bool match = evt.name.ToLower().Contains(filterLower) ||
                                 evt.shaderName.ToLower().Contains(filterLower) ||
                                 evt.materialName.ToLower().Contains(filterLower) ||
                                 evt.batchBreakCause.ToLower().Contains(filterLower) ||
                                 evt.type.ToLower().Contains(filterLower);
                    if (!match) continue;
                }

                displayedCount++;

                // Styling row selection
                GUIStyle rowStyle = new GUIStyle(GUI.skin.label);
                if (selectedEvent == evt)
                {
                    rowStyle.normal.textColor = Color.cyan;
                    rowStyle.fontStyle = FontStyle.Bold;
                }

                GUILayout.BeginHorizontal();

                // Index
                GUILayout.Label(evt.index.ToString(), rowStyle, GUILayout.Width(35));

                // Indented Name
                string indentSpace = new string(' ', evt.level * 4);
                string displayName = indentSpace + evt.name;
                
                if (GUILayout.Button(displayName, rowStyle, GUILayout.ExpandWidth(true)))
                {
                    selectedEvent = evt;
                    GUI.FocusControl(null);
                }

                // Vertices
                string vertStr = evt.vertexCount > 0 ? evt.vertexCount.ToString("N0") : "-";
                GUILayout.Label(vertStr, rowStyle, GUILayout.Width(65));

                // Batch Break Cause (Reason why draw call happened)
                string reasonStr = evt.batchBreakCause == "None" ? "-" : evt.batchBreakCause;
                GUILayout.Label(reasonStr, rowStyle, GUILayout.Width(150));

                // Shader Name
                string shortShaderName = string.IsNullOrEmpty(evt.shaderName) ? "-" : Path.GetFileName(evt.shaderName);
                GUILayout.Label(shortShaderName, rowStyle, GUILayout.Width(100));

                GUILayout.EndHorizontal();
            }

            if (displayedCount == 0)
            {
                if (capturedEvents.Count == 0)
                {
                    GUILayout.Label("No captured frame data. Click 'Capture Frame' to profile.", EditorStyles.centeredGreyMiniLabel);
                }
                else
                {
                    GUILayout.Label("No events match the active filter.", EditorStyles.centeredGreyMiniLabel);
                }
            }

            EditorGUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private void DrawEventDetailsPanel()
        {
            GUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.ExpandHeight(true));
            GUILayout.Label("EVENT METADATA DETAILS", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider, GUILayout.Height(6));

            if (selectedEvent == null)
            {
                GUILayout.Label("Select an event from the list to view its non-public fields and property data.", EditorStyles.centeredGreyMiniLabel);
                GUILayout.EndVertical();
                return;
            }

            detailsScrollPos = EditorGUILayout.BeginScrollView(detailsScrollPos);

            // Print basic stats first
            GUILayout.Label($"Event Index: {selectedEvent.index}", EditorStyles.boldLabel);
            GUILayout.Label($"Event Name: {selectedEvent.name}", EditorStyles.boldLabel);
            
            if (selectedEvent.eventDataFields.ContainsKey("ResolvedGameObjectPath"))
            {
                GUILayout.Label($"GameObject: {selectedEvent.eventDataFields["ResolvedGameObjectPath"]}", EditorStyles.boldLabel);
            }
            if (selectedEvent.eventDataFields.ContainsKey("ResolvedMeshName"))
            {
                GUILayout.Label($"Mesh: {selectedEvent.eventDataFields["ResolvedMeshName"]}");
            }
            if (selectedEvent.eventDataFields.ContainsKey("ResolvedComponentType"))
            {
                GUILayout.Label($"Component: {selectedEvent.eventDataFields["ResolvedComponentType"]}");
            }
            
            GUILayout.Label($"Hierarchy Level (Depth): {selectedEvent.level}");
            if (selectedEvent.eventDataFields.ContainsKey("RenderPassPath"))
            {
                GUILayout.Label($"Render Pass: {selectedEvent.eventDataFields["RenderPassPath"]}");
            }

            GUILayout.Label($"Event Type: {selectedEvent.type}");
            GUILayout.Label($"Batch Break Cause: {selectedEvent.batchBreakCause}", EditorStyles.boldLabel);
            
            if (!string.IsNullOrEmpty(selectedEvent.shaderName))
            {
                GUILayout.Label($"Shader: {selectedEvent.shaderName}", EditorStyles.boldLabel);
            }
            if (!string.IsNullOrEmpty(selectedEvent.materialName))
            {
                GUILayout.Label($"Material: {selectedEvent.materialName}");
            }
            if (selectedEvent.vertexCount > 0)
            {
                GUILayout.Label($"Vertices: {selectedEvent.vertexCount:N0}");
            }

            GUILayout.Space(10);
            GUILayout.Label("Event Object Fields (Dynamic):", EditorStyles.boldLabel);
            foreach (var kvp in selectedEvent.eventFields)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label("  " + kvp.Key + ":", EditorStyles.boldLabel, GUILayout.Width(150));
                GUILayout.Label(kvp.Value, GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();
            }

            if (selectedEvent.eventDataFields.Count > 0)
            {
                GUILayout.Space(10);
                GUILayout.Label("Event Data Fields (Dynamic):", EditorStyles.boldLabel);
                foreach (var kvp in selectedEvent.eventDataFields)
                {
                    // Skip layout keys to keep it readable
                    if (kvp.Key == "ResolvedGameObjectPath" || kvp.Key == "ResolvedMeshName" || 
                        kvp.Key == "ResolvedComponentType" || kvp.Key == "RenderPassPath" ||
                        kvp.Key == "BatchBreakCauseString") continue;

                    GUILayout.BeginHorizontal();
                    GUILayout.Label("  " + kvp.Key + ":", EditorStyles.boldLabel, GUILayout.Width(150));
                    GUILayout.Label(kvp.Value, GUILayout.ExpandWidth(true));
                    GUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private void CaptureFrameData()
        {
            if (!isReflectionResolved) return;

            capturedEvents.Clear();
            selectedEvent = null;
            totalDrawCalls = 0;
            totalVertices = 0;
            uniqueShaders.Clear();

            // Force repaint of all editor views so the Frame Debugger captures a fresh, active frame
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();

            var tempEvents = new List<CapturedEvent>();
            // Try to capture from the TreeView first (to get depths and hierarchy names)
            bool treeViewSuccess = TryCaptureFromTreeView(tempEvents);

            if (!treeViewSuccess)
            {
                // Fallback to flat list query
                try
                {
                    Array eventsArray = (Array)getFrameEventsMethod.Invoke(null, null);
                    int count = eventsArray != null ? eventsArray.Length : GetEventCount();
                    
                    if (count == 0)
                    {
                        Debug.LogWarning("[SYN FRAME EXPORTER] No frame events captured. Is the editor rendering?");
                        return;
                    }

                    MethodInfo getFrameEventInfoNameMethod = frameDebuggerUtilityType.GetMethod("GetFrameEventInfoName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                    for (int i = 0; i < count; i++)
                    {
                        var cap = new CapturedEvent
                        {
                            index = i,
                            level = 0
                        };

                        if (getFrameEventInfoNameMethod != null)
                        {
                            cap.name = (string)getFrameEventInfoNameMethod.Invoke(null, new object[] { i });
                        }

                        if (eventsArray != null && i < eventsArray.Length)
                        {
                            object frameEvent = eventsArray.GetValue(i);
                            if (frameEvent != null)
                            {
                                FieldInfo[] fields = frameEvent.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                foreach (var f in fields)
                                {
                                    object val = f.GetValue(frameEvent);
                                    string valStr = GetValueString(val);
                                    cap.eventFields[f.Name] = valStr;

                                    string fieldNameLower = f.Name.ToLower();
                                    if (fieldNameLower == "type" || fieldNameLower == "m_type")
                                    {
                                        cap.type = valStr;
                                    }
                                }
                            }
                        }

                        if (string.IsNullOrEmpty(cap.name)) cap.name = $"Event {i}";
                        if (string.IsNullOrEmpty(cap.type)) cap.type = "Unknown Type";

                        tempEvents.Add(cap);
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[SYN FRAME EXPORTER] Failed during flat fallback capture: {e.Message}\n{e.StackTrace}");
                    return;
                }
            }

            // Post-Process hierarchy paths (Opaque, Transparent, etc.)
            List<string> activePath = new List<string>();
            for (int i = 0; i < tempEvents.Count; i++)
            {
                var evt = tempEvents[i];
                int depth = evt.level;
                
                while (activePath.Count > depth)
                {
                    activePath.RemoveAt(activePath.Count - 1);
                }
                
                while (activePath.Count < depth)
                {
                    activePath.Add("");
                }
                
                string parentPath = string.Join(" > ", activePath);
                if (!string.IsNullOrEmpty(parentPath))
                {
                    evt.eventDataFields["RenderPassPath"] = parentPath;
                }
                
                if (activePath.Count == depth)
                {
                    activePath.Add(evt.name);
                }
                else
                {
                    activePath[depth] = evt.name;
                }
            }

            StartAsyncCapture(tempEvents);
        }

        private void StartAsyncCapture(List<CapturedEvent> events)
        {
            if (events == null || events.Count == 0) return;

            isCapturingAsync = true;
            asyncCurrentIndex = 0;
            asyncTotalEvents = events.Count;
            asyncCapturedEvents = events;
            originalLimit = limitProp != null ? (int)limitProp.GetValue(null) : 0;
            frameDebuggerWindowInstance = null;
            changeLimitMethodInfo = null;

            // Resolve the FrameDebuggerWindow instance and ChangeFrameEventLimit method
            try
            {
                var assembly = typeof(EditorWindow).Assembly;
                Type windowType = assembly.GetType("UnityEditor.FrameDebuggerWindow");
                if (windowType != null)
                {
                    frameDebuggerWindowInstance = GetWindow(windowType, false, "Frame Debugger", true);
                    if (frameDebuggerWindowInstance != null)
                    {
                        ((EditorWindow)frameDebuggerWindowInstance).Focus();
                        changeLimitMethodInfo = windowType.GetMethod("ChangeFrameEventLimit", BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(int) }, null);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SYN FRAME EXPORTER] Failed to resolve FrameDebuggerWindow reflection: {ex.Message}");
            }

            EditorApplication.update += UpdateAsyncCapture;
        }

        private void UpdateAsyncCapture()
        {
            if (!isCapturingAsync) return;

            // Wait 3 frames after setting the limit in the previous step to let the GPU render
            if (asyncCurrentIndex > 0)
            {
                if (asyncFrameWaitCount < 3)
                {
                    asyncFrameWaitCount++;
                    UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
                    return; // Wait for next frame
                }

                // 3 frames have passed, extract details
                int prevIndex = asyncCurrentIndex - 1;
                var cap = asyncCapturedEvents[prevIndex];
                ExtractEventData(cap.index, cap);
            }

            if (asyncCurrentIndex >= asyncTotalEvents)
            {
                // Finished!
                isCapturingAsync = false;
                EditorApplication.update -= UpdateAsyncCapture;

                // Restore original limit
                if (limitProp != null)
                {
                    limitProp.SetValue(null, originalLimit);
                }

                // Copy to main list
                capturedEvents = new List<CapturedEvent>(asyncCapturedEvents);

                // Focus the exporter window again
                var exporterWindow = GetWindow<SynFrameDebuggerExporterWindow>("Syn Frame Exporter", true);
                if (exporterWindow != null)
                {
                    exporterWindow.Focus();
                }

                Debug.Log($"[SYN FRAME EXPORTER] Asynchronously captured {capturedEvents.Count} graphics events successfully.");
                EditorUtility.DisplayDialog("Capture Complete", $"Successfully captured {capturedEvents.Count} events with full GameObject details!", "OK");
                return;
            }

            // Set selection and limit using ChangeFrameEventLimit on FrameDebuggerWindow
            int targetIndex = asyncCapturedEvents[asyncCurrentIndex].index;
            bool limitSet = false;

            if (frameDebuggerWindowInstance != null && changeLimitMethodInfo != null)
            {
                try
                {
                    changeLimitMethodInfo.Invoke(frameDebuggerWindowInstance, new object[] { targetIndex });
                    limitSet = true;
                }
                catch (Exception ex)
                {
                    if (asyncCurrentIndex == 0)
                    {
                        Debug.LogError($"[SYN FRAME EXPORTER] Exception invoking ChangeFrameEventLimit: {ex.Message}\n{ex.StackTrace}");
                    }
                }
            }

            if (!limitSet && limitProp != null)
            {
                if (asyncCurrentIndex == 0)
                {
                    Debug.Log($"[SYN FRAME EXPORTER] Falling back to manual limit setting for index {targetIndex}");
                }
                limitProp.SetValue(null, targetIndex);
            }

            // Force repaint to process the change
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();

            asyncCurrentIndex++;
            asyncFrameWaitCount = 0;
        }

        private bool TryCaptureFromTreeView(List<CapturedEvent> eventsList)
        {
            try
            {
                var assembly = typeof(EditorWindow).Assembly;
                Type windowType = assembly.GetType("UnityEditor.FrameDebuggerWindow");
                if (windowType == null)
                {
                    Debug.LogWarning("[SYN FRAME EXPORTER] FrameDebuggerWindow type not found");
                    return false;
                }

                // Find or open the window and focus it
                var window = GetWindow(windowType, false, "Frame Debugger", true);
                if (window == null)
                {
                    Debug.LogWarning("[SYN FRAME EXPORTER] FrameDebuggerWindow instance could not be retrieved");
                    return false;
                }

                window.Focus();

                window.Repaint();

                FieldInfo treeViewField = windowType.GetField("m_TreeView", BindingFlags.NonPublic | BindingFlags.Instance);
                if (treeViewField == null)
                {
                    Debug.LogWarning("[SYN FRAME EXPORTER] Field m_TreeView not found on FrameDebuggerWindow");
                    return false;
                }

                object treeViewObj = treeViewField.GetValue(window);
                if (treeViewObj == null)
                {
                    Debug.LogWarning("[SYN FRAME EXPORTER] m_TreeView instance on FrameDebuggerWindow is null");
                    return false;
                }

                FieldInfo dataSourceField = treeViewObj.GetType().GetField("m_DataSource", BindingFlags.NonPublic | BindingFlags.Instance);
                if (dataSourceField == null)
                {
                    Debug.LogWarning("[SYN FRAME EXPORTER] Field m_DataSource not found on FrameDebuggerTreeView");
                    return false;
                }

                object dataSourceObj = dataSourceField.GetValue(treeViewObj);
                if (dataSourceObj == null)
                {
                    Debug.LogWarning("[SYN FRAME EXPORTER] m_DataSource instance is null");
                    return false;
                }

                Debug.Log($"[SYN FRAME EXPORTER] TryCaptureFromTreeView started. DataSource: {dataSourceObj.GetType().FullName}");

                // Get inner TreeViewController to resolve ReloadData
                FieldInfo innerTreeViewField = treeViewObj.GetType().GetField("m_TreeView", BindingFlags.NonPublic | BindingFlags.Instance);
                object innerTreeViewObj = innerTreeViewField != null ? innerTreeViewField.GetValue(treeViewObj) : null;

                // Resolve expansion methods supporting base class lookups
                Type dataSourceType = dataSourceObj.GetType();
                Type baseDataSourceType = dataSourceType.BaseType;
                Type treeViewType = innerTreeViewObj != null ? innerTreeViewObj.GetType() : treeViewObj.GetType();
                Type baseTreeViewType = treeViewType.BaseType;

                MethodInfo isExpandedMethod = dataSourceType.GetMethod("IsExpanded", new Type[] { typeof(int) }) ??
                                              (baseDataSourceType != null ? baseDataSourceType.GetMethod("IsExpanded", new Type[] { typeof(int) }) : null);

                MethodInfo setExpandedMethod = dataSourceType.GetMethod("SetExpanded", new Type[] { typeof(int), typeof(bool) }) ??
                                               (baseDataSourceType != null ? baseDataSourceType.GetMethod("SetExpanded", new Type[] { typeof(int), typeof(bool) }) : null);

                MethodInfo reloadDataMethod = dataSourceType.GetMethod("ReloadData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ??
                                              (baseDataSourceType != null ? baseDataSourceType.GetMethod("ReloadData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) : null);

                MethodInfo reloadTreeViewMethod = treeViewType.GetMethod("ReloadData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ??
                                                  (baseTreeViewType != null ? baseTreeViewType.GetMethod("ReloadData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) : null);

                MethodInfo getRowsMethod = dataSourceType.GetMethod("GetRows", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) ??
                                           (baseDataSourceType != null ? baseDataSourceType.GetMethod("GetRows", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) : null);

                if (isExpandedMethod == null || setExpandedMethod == null || reloadDataMethod == null || reloadTreeViewMethod == null || getRowsMethod == null)
                {
                    Debug.LogWarning($"[SYN FRAME EXPORTER] Missing essential TreeView navigation methods via reflection. isExpanded: {isExpandedMethod != null}, setExpanded: {setExpandedMethod != null}, reloadData: {reloadDataMethod != null}, reloadTreeView: {reloadTreeViewMethod != null}, getRows: {getRowsMethod != null}");
                    return false;
                }

                // Loop iteratively to recursively expand lazy-loaded tree view nodes
                bool expandedAny;
                int safetyCounter = 0;
                do
                {
                    expandedAny = false;

                    // Sync the row representation caches
                    reloadDataMethod.Invoke(dataSourceObj, null);
                    if (innerTreeViewObj != null)
                    {
                        reloadTreeViewMethod.Invoke(innerTreeViewObj, null);
                    }
                    else
                    {
                        reloadTreeViewMethod.Invoke(treeViewObj, null);
                    }

                    System.Collections.IList currentRows = (System.Collections.IList)getRowsMethod.Invoke(dataSourceObj, null);
                    if (currentRows == null || currentRows.Count == 0)
                    {
                        break;
                    }

                    foreach (object row in currentRows)
                    {
                        if (row == null) continue;

                        int id = (int)row.GetType().GetProperty("id", BindingFlags.Public | BindingFlags.Instance).GetValue(row);
                        bool hasChildren = (bool)row.GetType().GetProperty("hasChildren", BindingFlags.Public | BindingFlags.Instance).GetValue(row);

                        bool isExpanded = (bool)isExpandedMethod.Invoke(dataSourceObj, new object[] { id });
                        if (hasChildren && !isExpanded)
                        {
                            setExpandedMethod.Invoke(dataSourceObj, new object[] { id, true });
                            expandedAny = true;
                        }
                    }

                    safetyCounter++;
                    Debug.Log($"[SYN FRAME EXPORTER] Expansion pass {safetyCounter}. ExpandedAny: {expandedAny}");
                } while (expandedAny && safetyCounter < 15);

                // Final sync to make sure all newly generated rows are loaded
                reloadDataMethod.Invoke(dataSourceObj, null);
                if (innerTreeViewObj != null)
                {
                    reloadTreeViewMethod.Invoke(innerTreeViewObj, null);
                }
                else
                {
                    reloadTreeViewMethod.Invoke(treeViewObj, null);
                }

                System.Collections.IList rows = (System.Collections.IList)getRowsMethod.Invoke(dataSourceObj, null);
                if (rows == null || rows.Count == 0)
                {
                    Debug.LogWarning("[SYN FRAME EXPORTER] GetRows returned 0 rows");
                    return false;
                }

                Debug.Log($"[SYN FRAME EXPORTER] GetRows returned {rows.Count} rows after iterative expansion");

                for (int i = 0; i < rows.Count; i++)
                {
                    object row = rows[i];
                    if (row == null) continue;

                    // Extract properties
                    int depth = (int)row.GetType().GetProperty("depth", BindingFlags.Public | BindingFlags.Instance).GetValue(row);
                    string displayName = (string)row.GetType().GetProperty("displayName", BindingFlags.Public | BindingFlags.Instance).GetValue(row);
                    
                    int eventIndex = 0;
                    FieldInfo eventIndexField = row.GetType().GetField("m_EventIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (eventIndexField != null)
                    {
                        eventIndex = (int)eventIndexField.GetValue(row);
                    }

                    var cap = new CapturedEvent
                    {
                        index = eventIndex,
                        name = displayName,
                        level = depth
                    };

                    // Extract native event details
                    FieldInfo frameEventField = row.GetType().GetField("m_FrameEvent", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (frameEventField != null)
                    {
                        object frameEvent = frameEventField.GetValue(row);
                        if (frameEvent != null)
                        {
                            FieldInfo[] fields = frameEvent.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            foreach (var f in fields)
                            {
                                object val = f.GetValue(frameEvent);
                                string valStr = GetValueString(val);
                                cap.eventFields[f.Name] = valStr;

                                string fieldNameLower = f.Name.ToLower();
                                if (fieldNameLower == "type" || fieldNameLower == "m_type")
                                {
                                    cap.type = valStr;
                                }
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(cap.type)) cap.type = "Unknown Type";

                    eventsList.Add(cap);
                }

                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SYN FRAME EXPORTER] TreeView capture failed. Details: {e.Message}\n{e.StackTrace}");
                return false;
            }
        }

        private void ExtractEventData(int eventIndex, CapturedEvent cap)
        {
            try
            {
                object eventDataObj = null;

                // Query the window's details view cache (m_EventDetailsView.m_CurEventData.Value)
                if (frameDebuggerWindowInstance != null)
                {
                    var assembly = typeof(EditorWindow).Assembly;
                    Type windowType = assembly.GetType("UnityEditor.FrameDebuggerWindow");
                    
                    FieldInfo detailsViewField = windowType.GetField("m_EventDetailsView", BindingFlags.NonPublic | BindingFlags.Instance);
                    object detailsViewObj = detailsViewField != null ? detailsViewField.GetValue(frameDebuggerWindowInstance) : null;
                    
                    if (detailsViewObj != null)
                    {
                        FieldInfo curEventDataField = detailsViewObj.GetType().GetField("m_CurEventData", BindingFlags.NonPublic | BindingFlags.Instance);
                        object lazyObj = curEventDataField != null ? curEventDataField.GetValue(detailsViewObj) : null;
                        
                        if (lazyObj != null)
                        {
                            PropertyInfo valueProp = lazyObj.GetType().GetProperty("Value");
                            if (valueProp != null)
                            {
                                eventDataObj = valueProp.GetValue(lazyObj);
                            }
                        }
                    }
                }

                // Fallback: If we couldn't get it from cache, try calling GetFrameEventData directly
                if (eventDataObj == null && getFrameEventDataMethod != null)
                {
                    try
                    {
                        object tempEventData = Activator.CreateInstance(frameDebuggerEventDataType);
                        object[] eventDataArgs = new object[] { eventIndex, tempEventData };
                        bool hasData = (bool)getFrameEventDataMethod.Invoke(null, eventDataArgs);
                        if (hasData)
                        {
                            eventDataObj = eventDataArgs[1] ?? tempEventData;
                        }
                    }
                    catch (Exception) { }
                }

                if (eventIndex == 0 || eventIndex == 10 || eventIndex == 50 || eventIndex == 100)
                {
                    Debug.Log($"[SYN FRAME EXPORTER] ExtractEventData for index {eventIndex}: eventDataObj={(eventDataObj != null ? "Not Null" : "Null")}");
                }

                if (eventDataObj != null)
                {
                    FieldInfo[] dataFields = eventDataObj.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    foreach (var df in dataFields)
                        {
                            object val = df.GetValue(eventDataObj);
                            string valStr = GetValueString(val);
                            cap.eventDataFields[df.Name] = valStr;

                            string dfNameLower = df.Name.ToLower();

                            // Resolve component Instance ID to GameObject Scene Path and component type
                            if (dfNameLower == "m_componentinstanceid" && val is int compId && compId != 0)
                            {
                                var comp = EditorUtility.InstanceIDToObject(compId) as Component;
                                if (comp != null)
                                {
                                    cap.eventDataFields["ResolvedGameObjectPath"] = GetGameObjectPath(comp.gameObject);
                                    cap.eventDataFields["ResolvedComponentType"] = comp.GetType().Name;

                                    // Resolve material name from Renderer component
                                    if (comp is Renderer renderer)
                                    {
                                        if (renderer.sharedMaterial != null)
                                        {
                                            cap.materialName = renderer.sharedMaterial.name;
                                            cap.eventDataFields["ResolvedMaterialName"] = renderer.sharedMaterial.name;
                                        }
                                    }

                                    // Prepend GameObject name to event name if generic
                                    if (cap.name.StartsWith("Draw") || cap.name.StartsWith("Geometry") || cap.name.StartsWith("Mesh"))
                                    {
                                        cap.name = $"{cap.name} ({comp.gameObject.name})";
                                    }
                                }
                            }
                            else if (dfNameLower == "m_meshinstanceid" && val is int meshId && meshId != 0)
                            {
                                var mesh = EditorUtility.InstanceIDToObject(meshId) as Mesh;
                                if (mesh != null)
                                {
                                    cap.eventDataFields["ResolvedMeshName"] = mesh.name;
                                }
                            }
                            else if (dfNameLower == "m_shaderinstanceid" && val is int shaderId && shaderId != 0)
                            {
                                var shader = EditorUtility.InstanceIDToObject(shaderId) as Shader;
                                if (shader != null)
                                {
                                    cap.shaderName = shader.name;
                                    cap.eventDataFields["ResolvedShaderName"] = shader.name;
                                    uniqueShaders.Add(shader.name);
                                }
                            }
                            else if (dfNameLower.Contains("shader") && val is Shader shaderObj)
                            {
                                cap.shaderName = shaderObj.name;
                                uniqueShaders.Add(shaderObj.name);
                            }
                            else if (dfNameLower.Contains("material") && val is Material matObj)
                            {
                                cap.materialName = matObj.name;
                            }
                            else if (dfNameLower == "vertexcount" || dfNameLower == "m_vertexcount")
                            {
                                cap.vertexCount = Convert.ToInt32(val);
                                totalVertices += cap.vertexCount;
                            }
                            else if (dfNameLower == "drawcall" || dfNameLower == "m_drawcall" || dfNameLower == "m_drawcallcount")
                            {
                                if (Convert.ToBoolean(val))
                                {
                                    totalDrawCalls++;
                                }
                            }
                            else if (dfNameLower == "m_batchbreakcause" || dfNameLower == "batchbreakcause")
                            {
                                int causeIndex = Convert.ToInt32(val);
                                string causeStr = "None";
                                if (batchBreakCauses != null && causeIndex >= 0 && causeIndex < batchBreakCauses.Length)
                                {
                                    causeStr = batchBreakCauses[causeIndex];
                                }
                                cap.batchBreakCause = causeStr;
                                cap.eventDataFields["BatchBreakCauseString"] = causeStr;
                            }
                            else if (df.FieldType.Name.Contains("ShaderInfo"))
                            {
                                ExtractShaderInfo(val, cap);
                            }
                        }
                    }
                }
            catch (Exception ex)
            {
                Debug.LogError($"[SYN FRAME EXPORTER] Error extracting event data for index {eventIndex}: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void ExtractShaderInfo(object shaderInfo, CapturedEvent cap)
        {
            if (shaderInfo == null) return;

            try
            {
                FieldInfo keywordsField = shaderInfo.GetType().GetField("m_Keywords", BindingFlags.Public | BindingFlags.Instance);
                FieldInfo floatsField = shaderInfo.GetType().GetField("m_Floats", BindingFlags.Public | BindingFlags.Instance);
                FieldInfo intsField = shaderInfo.GetType().GetField("m_Ints", BindingFlags.Public | BindingFlags.Instance);
                FieldInfo vectorsField = shaderInfo.GetType().GetField("m_Vectors", BindingFlags.Public | BindingFlags.Instance);
                FieldInfo texturesField = shaderInfo.GetType().GetField("m_Textures", BindingFlags.Public | BindingFlags.Instance);

                if (keywordsField != null)
                {
                    Array keywords = (Array)keywordsField.GetValue(shaderInfo);
                    if (keywords != null && keywords.Length > 0)
                    {
                        var kwList = new List<string>();
                        foreach (var kw in keywords)
                        {
                            string kwName = (string)kw.GetType().GetField("m_Name", BindingFlags.Public | BindingFlags.Instance).GetValue(kw);
                            kwList.Add(kwName);
                        }
                        cap.eventDataFields["ShaderKeywordsList"] = string.Join(" ", kwList);
                    }
                }

                if (floatsField != null)
                {
                    Array floats = (Array)floatsField.GetValue(shaderInfo);
                    if (floats != null)
                    {
                        foreach (var fl in floats)
                        {
                            string name = (string)fl.GetType().GetField("m_Name", BindingFlags.Public | BindingFlags.Instance).GetValue(fl);
                            float value = (float)fl.GetType().GetField("m_Value", BindingFlags.Public | BindingFlags.Instance).GetValue(fl);
                            cap.eventDataFields[$"ShaderFloat_{name}"] = value.ToString();
                        }
                    }
                }

                if (intsField != null)
                {
                    Array ints = (Array)intsField.GetValue(shaderInfo);
                    if (ints != null)
                    {
                        foreach (var it in ints)
                        {
                            string name = (string)it.GetType().GetField("m_Name", BindingFlags.Public | BindingFlags.Instance).GetValue(it);
                            int value = (int)it.GetType().GetField("m_Value", BindingFlags.Public | BindingFlags.Instance).GetValue(it);
                            cap.eventDataFields[$"ShaderInt_{name}"] = value.ToString();
                        }
                    }
                }

                if (vectorsField != null)
                {
                    Array vectors = (Array)vectorsField.GetValue(shaderInfo);
                    if (vectors != null)
                    {
                        foreach (var vec in vectors)
                        {
                            string name = (string)vec.GetType().GetField("m_Name", BindingFlags.Public | BindingFlags.Instance).GetValue(vec);
                            Vector4 value = (Vector4)vec.GetType().GetField("m_Value", BindingFlags.Public | BindingFlags.Instance).GetValue(vec);
                            cap.eventDataFields[$"ShaderVector_{name}"] = value.ToString();
                        }
                    }
                }

                if (texturesField != null)
                {
                    Array textures = (Array)texturesField.GetValue(shaderInfo);
                    if (textures != null)
                    {
                        foreach (var tex in textures)
                        {
                            string name = (string)tex.GetType().GetField("m_Name", BindingFlags.Public | BindingFlags.Instance).GetValue(tex);
                            string texName = (string)tex.GetType().GetField("m_TextureName", BindingFlags.Public | BindingFlags.Instance).GetValue(tex);
                            cap.eventDataFields[$"ShaderTexture_{name}"] = texName;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SYN FRAME EXPORTER] Failed to extract shader info details: " + e.Message);
            }
        }

        private static string GetGameObjectPath(GameObject go)
        {
            if (go == null) return "";
            string path = go.name;
            while (go.transform.parent != null)
            {
                go = go.transform.parent.gameObject;
                path = go.name + "/" + path;
            }
            return path;
        }

        private static string GetValueString(object val)
        {
            if (val == null) return "null";
            if (val is Shader s) return s.name;
            if (val is Material m) return m.name;
            if (val is Renderer r) return $"{r.name} ({r.GetType().Name})";
            if (val is Texture t) return $"{t.name} ({t.width}x{t.height})";
            if (val is GameObject go) return go.name;
            return val.ToString();
        }

        private void ExportCSV()
        {
            string path = EditorUtility.SaveFilePanel("Export Frame Data to CSV", "", $"FrameData_{DateTime.Now:yyyyMMdd_HHmmss}.csv", "csv");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Index,Depth,Type,Name,GameObjectPath,Shader,Material,Vertices,BatchBreakCause,RenderPassPath,DrawCallFields");

                foreach (var evt in capturedEvents)
                {
                    // Escape CSV fields
                    string nameEsc = EscapeCSV(evt.name);
                    string typeEsc = EscapeCSV(evt.type);
                    string shaderEsc = EscapeCSV(evt.shaderName);
                    string matEsc = EscapeCSV(evt.materialName);
                    string causeEsc = EscapeCSV(evt.batchBreakCause);
                    
                    string goPath = evt.eventDataFields.ContainsKey("ResolvedGameObjectPath") ? evt.eventDataFields["ResolvedGameObjectPath"] : "";
                    string goPathEsc = EscapeCSV(goPath);

                    string passPath = evt.eventDataFields.ContainsKey("RenderPassPath") ? evt.eventDataFields["RenderPassPath"] : "";
                    string passPathEsc = EscapeCSV(passPath);

                    // Combine all extra fields into a details column
                    var details = new List<string>();
                    foreach (var kvp in evt.eventFields) details.Add($"{kvp.Key}={kvp.Value}");
                    foreach (var kvp in evt.eventDataFields)
                    {
                        if (kvp.Key == "ResolvedGameObjectPath" || kvp.Key == "RenderPassPath") continue;
                        details.Add($"{kvp.Key}={kvp.Value}");
                    }
                    string detailsEsc = EscapeCSV(string.Join(" | ", details));

                    sb.AppendLine($"{evt.index},{evt.level},{typeEsc},{nameEsc},{goPathEsc},{shaderEsc},{matEsc},{evt.vertexCount},{causeEsc},{passPathEsc},\"{detailsEsc}\"");
                }

                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                Debug.Log($"[SYN FRAME EXPORTER] CSV exported to: {path}");
                EditorUtility.DisplayDialog("Export Complete", $"CSV file saved successfully to:\n{path}", "OK");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SYN FRAME EXPORTER] Failed to export CSV: {e.Message}");
            }
        }

        private string EscapeCSV(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.Contains(",") || text.Contains("\"") || text.Contains("\n") || text.Contains("\r"))
            {
                return "\"" + text.Replace("\"", "\"\"") + "\"";
            }
            return text;
        }

        private void ExportJSON()
        {
            string path = EditorUtility.SaveFilePanel("Export Frame Data to JSON", "", $"FrameData_{DateTime.Now:yyyyMMdd_HHmmss}.json", "json");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("[");
                for (int i = 0; i < capturedEvents.Count; i++)
                {
                    var evt = capturedEvents[i];
                    sb.AppendLine("  {");
                    sb.AppendLine($"    \"index\": {evt.index},");
                    sb.AppendLine($"    \"level\": {evt.level},");
                    sb.AppendLine($"    \"type\": \"{EscapeJSON(evt.type)}\",");
                    sb.AppendLine($"    \"name\": \"{EscapeJSON(evt.name)}\",");
                    sb.AppendLine($"    \"shaderName\": \"{EscapeJSON(evt.shaderName)}\",");
                    sb.AppendLine($"    \"materialName\": \"{EscapeJSON(evt.materialName)}\",");
                    sb.AppendLine($"    \"vertexCount\": {evt.vertexCount},");
                    sb.AppendLine($"    \"batchBreakCause\": \"{EscapeJSON(evt.batchBreakCause)}\",");

                    string goPath = evt.eventDataFields.ContainsKey("ResolvedGameObjectPath") ? evt.eventDataFields["ResolvedGameObjectPath"] : "";
                    sb.AppendLine($"    \"gameObjectPath\": \"{EscapeJSON(goPath)}\",");

                    string passPath = evt.eventDataFields.ContainsKey("RenderPassPath") ? evt.eventDataFields["RenderPassPath"] : "";
                    sb.AppendLine($"    \"renderPassPath\": \"{EscapeJSON(passPath)}\",");

                    // Event properties
                    sb.AppendLine("    \"eventFields\": {");
                    int k1 = 0;
                    foreach (var kvp in evt.eventFields)
                    {
                        string comma = ++k1 < evt.eventFields.Count ? "," : "";
                        sb.AppendLine($"      \"{EscapeJSON(kvp.Key)}\": \"{EscapeJSON(kvp.Value)}\"{comma}");
                    }
                    sb.AppendLine("    },");

                    // Event Data properties
                    sb.AppendLine("    \"eventDataFields\": {");
                    var exportedFields = new List<KeyValuePair<string, string>>();
                    foreach (var kvp in evt.eventDataFields)
                    {
                        if (kvp.Key == "ResolvedGameObjectPath" || kvp.Key == "RenderPassPath") continue;
                        exportedFields.Add(kvp);
                    }
                    for (int k2 = 0; k2 < exportedFields.Count; k2++)
                    {
                        var kvp = exportedFields[k2];
                        string comma = k2 < exportedFields.Count - 1 ? "," : "";
                        sb.AppendLine($"      \"{EscapeJSON(kvp.Key)}\": \"{EscapeJSON(kvp.Value)}\"{comma}");
                    }
                    sb.AppendLine("    }");

                    string endComma = i < capturedEvents.Count - 1 ? "," : "";
                    sb.AppendLine("  }" + endComma);
                }
                sb.AppendLine("]");

                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                Debug.Log($"[SYN FRAME EXPORTER] JSON exported to: {path}");
                EditorUtility.DisplayDialog("Export Complete", $"JSON file saved successfully to:\n{path}", "OK");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SYN FRAME EXPORTER] Failed to export JSON: {e.Message}");
            }
        }

        private string EscapeJSON(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        private void ExportTXT()
        {
            string path = EditorUtility.SaveFilePanel("Export Frame Data to TXT", "", $"FrameData_{DateTime.Now:yyyyMMdd_HHmmss}.txt", "txt");
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("SYN FRAME DEBUGGER EXPORTER - TEXT SUMMARY");
                sb.AppendLine($"Exported: {DateTime.Now}");
                sb.AppendLine($"Total Events: {capturedEvents.Count}");
                sb.AppendLine($"Total Draw Calls: {totalDrawCalls}");
                sb.AppendLine($"Total Vertices: {totalVertices:N0}");
                sb.AppendLine($"Unique Shaders: {uniqueShaders.Count}");
                sb.AppendLine(new string('=', 60));
                sb.AppendLine();

                foreach (var evt in capturedEvents)
                {
                    string indent = new string(' ', evt.level * 4);
                    string shaderSuffix = string.IsNullOrEmpty(evt.shaderName) ? "" : $"  [Shader: {evt.shaderName}]";
                    string matSuffix = string.IsNullOrEmpty(evt.materialName) ? "" : $" [Mat: {evt.materialName}]";
                    string vertSuffix = evt.vertexCount > 0 ? $" [Verts: {evt.vertexCount:N0}]" : "";
                    string causeSuffix = evt.batchBreakCause == "None" ? "" : $" [BatchBreak: {evt.batchBreakCause}]";
                    
                    string goPath = evt.eventDataFields.ContainsKey("ResolvedGameObjectPath") ? evt.eventDataFields["ResolvedGameObjectPath"] : "";
                    string goSuffix = string.IsNullOrEmpty(goPath) ? "" : $" [GameObject: {goPath}]";

                    sb.AppendLine($"{indent}[{evt.index}] {evt.name} ({evt.type}){shaderSuffix}{matSuffix}{vertSuffix}{causeSuffix}{goSuffix}");
                }

                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                Debug.Log($"[SYN FRAME EXPORTER] TXT exported to: {path}");
                EditorUtility.DisplayDialog("Export Complete", $"TXT file saved successfully to:\n{path}", "OK");
            }
            catch (Exception e)
            {
                Debug.LogError($"[SYN FRAME EXPORTER] Failed to export TXT: {e.Message}");
            }
        }
    }
}
