using System;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Synthos.SynSceneOptimizer
{
    public static class FrameDebuggerReflectionDumper
    {
        [MenuItem("Window/Synthos/Debug/Dump Frame Debugger API", priority = 101)]
        [MenuItem("Tools/Synthos/Debug/Dump Frame Debugger API", priority = 101)]
        public static void DumpFrameDebuggerAPI()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== FRAME DEBUGGER REFLECTION DUMP ===");
            sb.AppendLine($"Generated at: {DateTime.Now}");
            sb.AppendLine();

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (var assembly in assemblies)
            {
                // We are primarily interested in UnityEditor and UnityEngine assemblies
                string name = assembly.GetName().Name;
                if (!name.Contains("Unity")) continue;

                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException re)
                {
                    types = re.Types;
                }
                catch (Exception)
                {
                    continue;
                }

                if (types == null) continue;

                foreach (var type in types)
                {
                    if (type == null) continue;

                    string typeName = type.FullName ?? type.Name;
                    bool isMatch = typeName.IndexOf("FrameDebugger", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   typeName.IndexOf("FrameEvent", StringComparison.OrdinalIgnoreCase) >= 0;

                    if (isMatch)
                    {
                        DumpTypeInfo(sb, type);
                    }
                }
            }

            string outputPath = EditorUtility.SaveFilePanel("Save Frame Debugger Reflection Dump", Application.dataPath, "FrameDebuggerReflectionDump.txt", "txt");
            if (string.IsNullOrEmpty(outputPath)) return;
            File.WriteAllText(outputPath, sb.ToString());
            Debug.Log("[FRAME DEBUGGER DUMPER] API successfully dumped to: " + outputPath);
        }

        private static void DumpTypeInfo(StringBuilder sb, Type type)
        {
            sb.AppendLine("=========================================================================");
            sb.AppendLine($"TYPE: {type.FullName}");
            sb.AppendLine($"Base Type: {type.BaseType?.FullName ?? "None"}");
            sb.AppendLine($"Is ValueType (Struct): {type.IsValueType}");
            sb.AppendLine($"Is Class: {type.IsClass}");
            sb.AppendLine($"Assembly: {type.Assembly.GetName().Name}");
            sb.AppendLine("=========================================================================");

            sb.AppendLine("--- FIELDS ---");
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            foreach (var field in fields)
            {
                sb.AppendLine($"  {(field.IsPublic ? "public" : "private")} {(field.IsStatic ? "static" : "")} {field.FieldType.FullName} {field.Name}");
            }

            sb.AppendLine();
            sb.AppendLine("--- PROPERTIES ---");
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            foreach (var prop in props)
            {
                sb.AppendLine($"  {prop.PropertyType.FullName} {prop.Name} (Get: {prop.CanRead}, Set: {prop.CanWrite})");
            }

            sb.AppendLine();
            sb.AppendLine("--- METHODS ---");
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
            foreach (var method in methods)
            {
                // Skip compiler generated property accessors to keep it clean
                if (method.Name.StartsWith("get_") || method.Name.StartsWith("set_")) continue;

                var parameters = method.GetParameters();
                var paramList = new StringBuilder();
                for (int i = 0; i < parameters.Length; i++)
                {
                    paramList.Append($"{parameters[i].ParameterType.Name} {parameters[i].Name}");
                    if (i < parameters.Length - 1) paramList.Append(", ");
                }

                sb.AppendLine($"  {(method.IsPublic ? "public" : "private")} {(method.IsStatic ? "static" : "")} {method.ReturnType.FullName} {method.Name}({paramList})");
            }

            sb.AppendLine();
            sb.AppendLine();
        }
    }
}
