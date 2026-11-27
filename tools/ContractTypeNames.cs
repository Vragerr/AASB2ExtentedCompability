using System.Collections.Immutable;
using System.Reflection.Metadata;

public sealed class ContractTypeNames : ISignatureTypeProvider<string, object>
{
    public string GetPrimitiveType(PrimitiveTypeCode code) => code.ToString();
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte kind)
    {
        var type = reader.GetTypeDefinition(handle);
        return FullName(reader.GetString(type.Namespace), reader.GetString(type.Name));
    }
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte kind)
    {
        var type = reader.GetTypeReference(handle);
        return FullName(reader.GetString(type.Namespace), reader.GetString(type.Name));
    }
    public string GetTypeFromSpecification(MetadataReader reader, object context, TypeSpecificationHandle handle, byte kind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, context);
    public string GetSZArrayType(string type) => type + "[]";
    public string GetArrayType(string type, ArrayShape shape) => type + "[" + new string(',', shape.Rank - 1) + "]";
    public string GetByReferenceType(string type) => type + "&";
    public string GetPointerType(string type) => type + "*";
    public string GetPinnedType(string type) => type + " pinned";
    public string GetGenericMethodParameter(object context, int index) => "!!" + index;
    public string GetGenericTypeParameter(object context, int index) => "!" + index;
    public string GetModifiedType(string modifier, string type, bool required)
        => type + (required ? " modreq(" : " modopt(") + modifier + ")";
    public string GetGenericInstantiation(string type, ImmutableArray<string> args)
        => type + "<" + string.Join(", ", args) + ">";
    public string GetFunctionPointerType(MethodSignature<string> signature)
        => "fnptr " + signature.ReturnType + "(" + string.Join(", ", signature.ParameterTypes) + ")";
    private static string FullName(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;
}
