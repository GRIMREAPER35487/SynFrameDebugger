using UnityEditor;
using UnityEngine;
using System;
using System.Reflection;

namespace Synthos.SynSceneOptimizer
{
    public class SynFrameDebuggerDebugTool : EditorWindow
    {
        [MenuItem("Window/Synthos/Debug/Debug Frame Details", priority = 100)]
        [MenuItem("Tools/Synthos/Debug/Debug Frame Details", priority = 100)]
        public static void DebugFrameDetails()
        {
            try
            {
                var assembly = typeof(EditorWindow).Assembly;
                Type windowType = assembly.GetType("UnityEditor.FrameDebuggerWindow");
                if (windowType == null)
                {
                    Debug.LogError("FrameDebuggerWindow type not found");
                    return;
                }

                var window = GetWindow(windowType, false, "Frame Debugger", false);
                if (window == null)
                {
                    Debug.LogError("FrameDebuggerWindow instance not found");
                    return;
                }

                Debug.Log($"[SYN DEBUG TOOL] FrameDebuggerWindow instance: {window}");

                FieldInfo detailsViewField = windowType.GetField("m_EventDetailsView", BindingFlags.NonPublic | BindingFlags.Instance);
                if (detailsViewField == null)
                {
                    Debug.LogError("[SYN DEBUG TOOL] m_EventDetailsView field not found");
                    return;
                }

                object detailsViewObj = detailsViewField.GetValue(window);
                if (detailsViewObj == null)
                {
                    Debug.LogError("[SYN DEBUG TOOL] m_EventDetailsView instance is null");
                    return;
                }

                Debug.Log($"[SYN DEBUG TOOL] m_EventDetailsView instance: {detailsViewObj}");

                FieldInfo curEventDataField = detailsViewObj.GetType().GetField("m_CurEventData", BindingFlags.NonPublic | BindingFlags.Instance);
                if (curEventDataField == null)
                {
                    Debug.LogError("[SYN DEBUG TOOL] m_CurEventData field not found");
                    return;
                }

                object lazyObj = curEventDataField.GetValue(detailsViewObj);
                if (lazyObj == null)
                {
                    Debug.LogError("[SYN DEBUG TOOL] m_CurEventData instance is null");
                    return;
                }

                Debug.Log($"[SYN DEBUG TOOL] m_CurEventData lazy instance: {lazyObj}");

                PropertyInfo isValueCreatedProp = lazyObj.GetType().GetProperty("IsValueCreated");
                bool isValueCreated = (bool)isValueCreatedProp.GetValue(lazyObj);
                Debug.Log($"[SYN DEBUG TOOL] m_CurEventData.IsValueCreated: {isValueCreated}");

                PropertyInfo valueProp = lazyObj.GetType().GetProperty("Value");
                object eventDataObj = valueProp.GetValue(lazyObj);
                if (eventDataObj == null)
                {
                    Debug.LogError("[SYN DEBUG TOOL] m_CurEventData.Value is null!");
                    return;
                }

                Debug.Log($"[SYN DEBUG TOOL] m_CurEventData.Value instance: {eventDataObj}");

                FieldInfo[] fields = eventDataObj.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var f in fields)
                {
                    object val = f.GetValue(eventDataObj);
                    Debug.Log($"[SYN DEBUG TOOL]   Field: {f.Name} = {val}");
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SYN DEBUG TOOL] Error in DebugFrameDetails: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}
