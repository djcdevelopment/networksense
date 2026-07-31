using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace SyntheticBaselineExtractor
{
    class Program
    {
        static void Main(string[] args)
        {
            string assemblyPath = args.Length > 0
                ? args[0]
                : @"C:\Program Files (x86)\Steam\steamapps\common\Valheim\valheim_Data\Managed\assembly_valheim.dll";

            string outputPath = args.Length > 1
                ? args[1]
                : Path.Combine(GetToolDirectory(), "synthetic_baseline_v2.json");

            if (!File.Exists(assemblyPath))
            {
                Console.WriteLine($"Error: Cannot find {assemblyPath}");
                return;
            }

            Console.WriteLine($"Analyzing {assemblyPath}...");

            var module = ModuleDefinition.ReadModule(assemblyPath);

            var routedRpcs = new Dictionary<string, RpcAccumulator>();
            var directRpcs = new Dictionary<string, RpcAccumulator>();
            var instanceRpcs = new Dictionary<string, RpcAccumulator>();
            var componentsWithZNetView = new HashSet<string>();
            var unresolved = new List<UnresolvedEntry>();

            foreach (var type in module.Types)
            {
                // Check if type interacts with ZNetView: own field, inherited field (walk BaseType
                // chain within this module), or a GetComponent<ZNetView>/<ZSyncTransform> call.
                bool usesZNetView = false;
                foreach (var field in type.Fields)
                {
                    if (field.FieldType.Name == "ZNetView" || field.FieldType.Name == "ZSyncTransform")
                    {
                        usesZNetView = true;
                        break;
                    }
                }

                if (!usesZNetView && HasZNetViewFieldInBaseChain(type, module))
                {
                    usesZNetView = true;
                }

                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;

                    foreach (var instr in method.Body.Instructions)
                    {
                        if (instr.OpCode != OpCodes.Call && instr.OpCode != OpCodes.Callvirt) continue;

                        // Look for calls to GetComponent<ZNetView>/<ZSyncTransform>
                        if (instr.Operand is GenericInstanceMethod gim &&
                            gim.GenericArguments.Any(a => a.Name == "ZNetView" || a.Name == "ZSyncTransform"))
                        {
                            usesZNetView = true;
                        }

                        // Look for RPC registrations:
                        //   ZRoutedRpc.Register(...) -> routed (broadcast/relayed via server)
                        //   ZRpc.Register(...)       -> direct (peer-to-peer connection RPC)
                        //   ZNetView.Register(...)   -> per-instance (bound to one ZDO/object)
                        if (instr.Operand is MethodReference methodRef && methodRef.Name == "Register")
                        {
                            string? kind = methodRef.DeclaringType?.Name switch
                            {
                                "ZRoutedRpc" => "ZRoutedRpc",
                                "ZRpc" => "ZRpc",
                                "ZNetView" => "ZNetView",
                                _ => null
                            };

                            if (kind != null)
                            {
                                var bucket = kind switch
                                {
                                    "ZRoutedRpc" => routedRpcs,
                                    "ZRpc" => directRpcs,
                                    _ => instanceRpcs
                                };

                                string owner = $"{type.Name}.{method.Name}";
                                string? rpcName = FindPushedString(instr);

                                // If Register<T,U,...> is a generic instantiation, the generic args are
                                // the RPC's payload types (the delegate's arguments beyond sender id).
                                List<string> signature = instr.Operand is GenericInstanceMethod regGim
                                    ? regGim.GenericArguments.Select(a => a.Name).ToList()
                                    : new List<string>();

                                if (rpcName != null)
                                {
                                    if (!bucket.TryGetValue(rpcName, out var acc))
                                    {
                                        acc = new RpcAccumulator();
                                        bucket[rpcName] = acc;
                                    }
                                    acc.Registrations.Add(owner);
                                    acc.AddSignature(signature);
                                }
                                else
                                {
                                    // Name resolution failed (Ldstr not found within the 10-instruction
                                    // backtrack window) - report it rather than silently dropping it, so
                                    // the audit knows the counts below are a floor, not a ceiling.
                                    unresolved.Add(new UnresolvedEntry { Declaring = owner, RegisterKind = kind });
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

            var output = new RootOutput
            {
                GeneratedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                AssemblySha256 = ComputeSha256(assemblyPath),
                RoutedRPCs = ToOrderedDict(routedRpcs),
                DirectRPCs = ToOrderedDict(directRpcs),
                InstanceRPCs = ToOrderedDict(instanceRpcs),
                ComponentsWithZNetView = componentsWithZNetView.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                UnresolvedRegistrations = unresolved
                    .OrderBy(u => u.Declaring, StringComparer.Ordinal)
                    .ThenBy(u => u.RegisterKind, StringComparer.Ordinal)
                    .ToList()
            };

            output.Counts = new CountsEntry
            {
                Routed = output.RoutedRPCs.Count,
                Direct = output.DirectRPCs.Count,
                Instance = output.InstanceRPCs.Count,
                Components = output.ComponentsWithZNetView.Count,
                Unresolved = output.UnresolvedRegistrations.Count
            };

            var serializerOptions = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            string json = JsonSerializer.Serialize(output, serializerOptions);
            File.WriteAllText(outputPath, json);

            Console.WriteLine($"Extracted {output.Counts.Routed} RoutedRPCs");
            Console.WriteLine($"Extracted {output.Counts.Direct} Direct RPCs");
            Console.WriteLine($"Extracted {output.Counts.Instance} Instance RPCs");
            Console.WriteLine($"Found {output.Counts.Components} Components using ZNetView");
            Console.WriteLine($"Found {output.Counts.Unresolved} unresolved registrations (name not statically resolvable)");
            Console.WriteLine($"Saved to {outputPath}");
        }

        static SortedDictionary<string, RpcEntry> ToOrderedDict(Dictionary<string, RpcAccumulator> src)
        {
            var result = new SortedDictionary<string, RpcEntry>(StringComparer.Ordinal);
            foreach (var kvp in src)
            {
                result[kvp.Key] = kvp.Value.ToEntry();
            }
            return result;
        }

        // Requirement 5: a type also "uses" ZNetView if any ancestor (within this module - we stop
        // the walk the moment BaseType isn't a TypeDefinition belonging to this module, e.g. once it
        // reaches UnityEngine.MonoBehaviour) declares a ZNetView/ZSyncTransform field itself.
        static bool HasZNetViewFieldInBaseChain(TypeDefinition type, ModuleDefinition module)
        {
            var current = type.BaseType;
            while (current is TypeDefinition baseDef && baseDef.Module == module)
            {
                if (baseDef.Fields.Any(f => f.FieldType.Name == "ZNetView" || f.FieldType.Name == "ZSyncTransform"))
                {
                    return true;
                }
                current = baseDef.BaseType;
            }
            return false;
        }

        // Captures the absolute path of THIS source file at compile time, so the default output
        // location tracks the tool directory regardless of process cwd or how it was invoked
        // (dotnet run vs. running the built exe from bin/).
        static string GetToolDirectory([CallerFilePath] string sourceFilePath = "")
        {
            return Path.GetDirectoryName(sourceFilePath) ?? Directory.GetCurrentDirectory();
        }

        static string ComputeSha256(string path)
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(path);
            byte[] hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        static string? FindPushedString(Instruction callInstr)
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

    // Accumulates every call site for one RPC name within a single category (Routed/Direct/Instance).
    class RpcAccumulator
    {
        public SortedSet<string> Registrations { get; } = new(StringComparer.Ordinal);
        private readonly List<List<string>> _signatures = new();

        public void AddSignature(List<string> signature)
        {
            if (!_signatures.Any(s => s.SequenceEqual(signature)))
            {
                _signatures.Add(signature);
            }
        }

        public RpcEntry ToEntry()
        {
            var entry = new RpcEntry { Registrations = Registrations.ToList() };
            if (_signatures.Count <= 1)
            {
                // Common case: zero or one distinct signature seen across all call sites.
                entry.Signature = _signatures.Count == 1 ? _signatures[0] : new List<string>();
            }
            else
            {
                // Rare case: the same RPC name was registered with different payload shapes at
                // different call sites. Keep the union rather than picking one arbitrarily.
                entry.Signatures = _signatures
                    .OrderBy(s => string.Join(",", s), StringComparer.Ordinal)
                    .ToList();
            }
            return entry;
        }
    }

    class RpcEntry
    {
        [JsonPropertyName("registrations")]
        public List<string> Registrations { get; set; } = new();

        [JsonPropertyName("signature")]
        public List<string>? Signature { get; set; }

        [JsonPropertyName("signatures")]
        public List<List<string>>? Signatures { get; set; }
    }

    class UnresolvedEntry
    {
        [JsonPropertyName("declaring")]
        public string Declaring { get; set; } = "";

        [JsonPropertyName("register_kind")]
        public string RegisterKind { get; set; } = "";
    }

    class CountsEntry
    {
        [JsonPropertyName("routed")]
        public int Routed { get; set; }

        [JsonPropertyName("direct")]
        public int Direct { get; set; }

        [JsonPropertyName("instance")]
        public int Instance { get; set; }

        [JsonPropertyName("components")]
        public int Components { get; set; }

        [JsonPropertyName("unresolved")]
        public int Unresolved { get; set; }
    }

    class RootOutput
    {
        [JsonPropertyName("generated_utc")]
        public string GeneratedUtc { get; set; } = "";

        [JsonPropertyName("assembly_sha256")]
        public string AssemblySha256 { get; set; } = "";

        [JsonPropertyName("RoutedRPCs")]
        public SortedDictionary<string, RpcEntry> RoutedRPCs { get; set; } = new();

        [JsonPropertyName("DirectRPCs")]
        public SortedDictionary<string, RpcEntry> DirectRPCs { get; set; } = new();

        [JsonPropertyName("InstanceRPCs")]
        public SortedDictionary<string, RpcEntry> InstanceRPCs { get; set; } = new();

        [JsonPropertyName("ComponentsWithZNetView")]
        public List<string> ComponentsWithZNetView { get; set; } = new();

        [JsonPropertyName("unresolved_registrations")]
        public List<UnresolvedEntry> UnresolvedRegistrations { get; set; } = new();

        [JsonPropertyName("counts")]
        public CountsEntry Counts { get; set; } = new();
    }
}
