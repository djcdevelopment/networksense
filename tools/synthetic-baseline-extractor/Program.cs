using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Text.Json;

namespace SyntheticBaselineExtractor
{
    class Program
    {
        static void Main(string[] args)
        {
            string assemblyPath = @"C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim_Data\Managed\assembly_valheim.dll";
            if (!File.Exists(assemblyPath))
            {
                Console.WriteLine($"Error: Cannot find {assemblyPath}");
                return;
            }

            Console.WriteLine($"Analyzing {assemblyPath}...");

            var module = ModuleDefinition.ReadModule(assemblyPath);

            var routedRpcs = new HashSet<string>();
            var directRpcs = new HashSet<string>();
            var componentsWithZNetView = new HashSet<string>();

            foreach (var type in module.Types)
            {
                // Check if type interacts with ZNetView (has it as a field or calls GetComponent<ZNetView>)
                bool usesZNetView = false;
                foreach (var field in type.Fields)
                {
                    if (field.FieldType.Name == "ZNetView" || field.FieldType.Name == "ZSyncTransform")
                    {
                        usesZNetView = true;
                        break;
                    }
                }

                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;

                    foreach (var instr in method.Body.Instructions)
                    {
                        // Look for calls to GetComponent<ZNetView>
                        if (instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt)
                        {
                            if (instr.Operand is GenericInstanceMethod gim && 
                                (gim.GenericArguments.Any(a => a.Name == "ZNetView" || a.Name == "ZSyncTransform")))
                            {
                                usesZNetView = true;
                            }
                        }

                        // Look for RPC Registrations
                        if (instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt)
                        {
                            if (instr.Operand is MethodReference methodRef)
                            {
                                if (methodRef.Name == "Register" && methodRef.DeclaringType.Name == "ZRoutedRpc")
                                {
                                    // Try to find the string pushed before this call
                                    string rpcName = FindPushedString(instr);
                                    if (rpcName != null)
                                    {
                                        routedRpcs.Add(rpcName);
                                    }
                                }
                                else if (methodRef.Name == "Register" && methodRef.DeclaringType.Name == "ZRpc")
                                {
                                    string rpcName = FindPushedString(instr);
                                    if (rpcName != null)
                                    {
                                        directRpcs.Add(rpcName);
                                    }
                                }
                            }
                        }
                    }
                }

                if (usesZNetView)
                {
                    componentsWithZNetView.Add(type.Name);
                }
            }

            var report = new
            {
                RoutedRPCs = routedRpcs.OrderBy(x => x).ToList(),
                DirectRPCs = directRpcs.OrderBy(x => x).ToList(),
                ComponentsWithZNetView = componentsWithZNetView.OrderBy(x => x).ToList()
            };

            string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText("synthetic_baseline.json", json);

            Console.WriteLine($"Extracted {routedRpcs.Count} RoutedRPCs");
            Console.WriteLine($"Extracted {directRpcs.Count} Direct RPCs");
            Console.WriteLine($"Found {componentsWithZNetView.Count} Components using ZNetView");
            Console.WriteLine("Saved to synthetic_baseline.json");
        }

        static string FindPushedString(Instruction callInstr)
        {
            // Backtrack to find Ldstr
            var current = callInstr.Previous;
            int depth = 0;
            while (current != null && depth < 10)
            {
                if (current.OpCode == OpCodes.Ldstr)
                {
                    return current.Operand as string;
                }
                current = current.Previous;
                depth++;
            }
            return null;
        }
    }
}
