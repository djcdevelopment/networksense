using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args.Length != 1)
{
    Console.Error.WriteLine("usage: NetworkSense.ReleaseInspector <assembly.dll>");
    return 2;
}

string assemblyPath = Path.GetFullPath(args[0]);
if (!File.Exists(assemblyPath))
{
    Console.Error.WriteLine($"assembly not found: {assemblyPath}");
    return 2;
}

try
{
    using FileStream stream = File.OpenRead(assemblyPath);
    using var pe = new PEReader(stream);
    if (!pe.HasMetadata)
    {
        throw new InvalidDataException("input is not a managed assembly");
    }

    MetadataReader metadata = pe.GetMetadataReader();
    AssemblyDefinition assembly = metadata.GetAssemblyDefinition();
    ModuleDefinition module = metadata.GetModuleDefinition();
    var assemblyMetadata = new SortedDictionary<string, string>(StringComparer.Ordinal);
    string? declaredFileVersion = null;

    foreach (CustomAttributeHandle handle in assembly.GetCustomAttributes())
    {
        CustomAttribute attribute = metadata.GetCustomAttribute(handle);
        (string Namespace, string Name) type = ReadAttributeType(metadata, attribute.Constructor);
        BlobReader value = metadata.GetBlobReader(attribute.Value);
        if (value.ReadUInt16() != 1)
        {
            throw new BadImageFormatException($"invalid custom-attribute prolog for {type.Name}");
        }

        if (type.Namespace == "System.Reflection" && type.Name == "AssemblyMetadataAttribute")
        {
            string key = value.ReadSerializedString()
                ?? throw new BadImageFormatException("AssemblyMetadata key is null");
            string metadataValue = value.ReadSerializedString() ?? string.Empty;
            assemblyMetadata[key] = metadataValue;
        }
        else if (type.Namespace == "System.Reflection" && type.Name == "AssemblyFileVersionAttribute")
        {
            declaredFileVersion = value.ReadSerializedString();
        }
    }

    var result = new InspectionResult
    {
        Schema = "comfy-mod-assembly-inspection/v1",
        AssemblyName = metadata.GetString(assembly.Name),
        AssemblyVersion = assembly.Version.ToString(),
        FileVersion = declaredFileVersion ?? FileVersionInfo.GetVersionInfo(assemblyPath).FileVersion ?? string.Empty,
        ModuleVersionId = metadata.GetGuid(module.Mvid).ToString("D"),
        Metadata = assemblyMetadata
    };

    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"release inspection failed: {exception.Message}");
    return 1;
}

static (string Namespace, string Name) ReadAttributeType(
    MetadataReader metadata,
    EntityHandle constructor)
{
    EntityHandle typeHandle = constructor.Kind switch
    {
        HandleKind.MemberReference => metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent,
        HandleKind.MethodDefinition => metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType(),
        _ => throw new BadImageFormatException($"unsupported attribute constructor: {constructor.Kind}")
    };

    return typeHandle.Kind switch
    {
        HandleKind.TypeReference => ReadTypeReference(metadata, (TypeReferenceHandle)typeHandle),
        HandleKind.TypeDefinition => ReadTypeDefinition(metadata, (TypeDefinitionHandle)typeHandle),
        _ => throw new BadImageFormatException($"unsupported attribute type: {typeHandle.Kind}")
    };
}

static (string Namespace, string Name) ReadTypeReference(
    MetadataReader metadata,
    TypeReferenceHandle handle)
{
    TypeReference type = metadata.GetTypeReference(handle);
    return (metadata.GetString(type.Namespace), metadata.GetString(type.Name));
}

static (string Namespace, string Name) ReadTypeDefinition(
    MetadataReader metadata,
    TypeDefinitionHandle handle)
{
    TypeDefinition type = metadata.GetTypeDefinition(handle);
    return (metadata.GetString(type.Namespace), metadata.GetString(type.Name));
}

internal sealed class InspectionResult
{
    [JsonPropertyName("schema")]
    public string Schema { get; init; } = string.Empty;

    [JsonPropertyName("assembly_name")]
    public string AssemblyName { get; init; } = string.Empty;

    [JsonPropertyName("assembly_version")]
    public string AssemblyVersion { get; init; } = string.Empty;

    [JsonPropertyName("file_version")]
    public string FileVersion { get; init; } = string.Empty;

    [JsonPropertyName("module_version_id")]
    public string ModuleVersionId { get; init; } = string.Empty;

    [JsonPropertyName("metadata")]
    public SortedDictionary<string, string> Metadata { get; init; } = new(StringComparer.Ordinal);
}
