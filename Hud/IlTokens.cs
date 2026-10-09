using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace ExileApiMcp.Hud;

/// <summary>
/// Resolve IL metadata tokens to MetadataLoadContext members. MetadataLoadContext can read IL bytes but can't resolve
/// tokens ("Resolving tokens is not supported on assemblies loaded by a MetadataLoadContext"), so this reads the token
/// tables with System.Reflection.Metadata and looks the members up by name in the HUD's MLC types:
/// FieldDef / MethodDef, MemberRef (incl. members of generic instantiations like CachedValue&lt;XOffsets&gt;), MethodSpec
/// (generic methods like Read&lt;T&gt;).
/// </summary>
public sealed class IlTokens
{
    private static readonly ConcurrentDictionary<string, IlTokens> ByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly MetadataReader _md;
    private readonly HudTypes _hud;
    private readonly PEReader _pe;

    private IlTokens(string path, HudTypes hud)
    {
        _pe = new PEReader(File.OpenRead(path));
        _md = _pe.GetMetadataReader();
        _hud = hud;
    }

    public static IlTokens For(Module module, HudTypes hud) =>
        ByPath.GetOrAdd(module.Assembly.Location + "|" + hud.GetHashCode(), _ => new IlTokens(module.Assembly.Location, hud));

    /// <summary>A resolved field: the declaring type and the field itself (both MLC objects).</summary>
    public FieldInfo? Field(int token)
    {
        var h = MetadataTokens.EntityHandle(token);
        if (h.Kind == HandleKind.FieldDefinition)
        {
            var fd = _md.GetFieldDefinition((FieldDefinitionHandle)h);
            return TypeOf(fd.GetDeclaringType())?.GetField(_md.GetString(fd.Name), All);
        }
        if (h.Kind == HandleKind.MemberReference)
        {
            var mr = _md.GetMemberReference((MemberReferenceHandle)h);
            return ParentType(mr.Parent)?.GetField(_md.GetString(mr.Name), All);
        }
        return null;
    }

    /// <summary>A resolved method plus, for generic instantiations, the type arguments that matter here.</summary>
    public (MethodBase? method, Type[] typeArgs, Type[] methodArgs) Method(int token)
    {
        var h = MetadataTokens.EntityHandle(token);
        switch (h.Kind)
        {
            case HandleKind.MethodDefinition:
            {
                var md = _md.GetMethodDefinition((MethodDefinitionHandle)h);
                var t = TypeOf(md.GetDeclaringType());
                return (FindMethod(t, _md.GetString(md.Name), md.GetParameters().Count), [], []);
            }
            case HandleKind.MemberReference:
            {
                var mr = _md.GetMemberReference((MemberReferenceHandle)h);
                var parent = ParentType(mr.Parent);
                var typeArgs = parent is { IsGenericType: true } ? Safe(() => parent.GetGenericArguments()) : [];
                var paramCount = mr.DecodeMethodSignature(Provider, null).ParameterTypes.Length;
                return (FindMethod(parent, _md.GetString(mr.Name), paramCount), typeArgs, []);
            }
            case HandleKind.MethodSpecification:
            {
                var ms = _md.GetMethodSpecification((MethodSpecificationHandle)h);
                var (m, targs, _) = Method(MetadataTokens.GetToken(ms.Method));
                var margs = ms.DecodeSignature(Provider, null).Select(x => x!).Where(x => x != null).ToArray();
                return (m, targs, margs);
            }
        }
        return (null, [], []);
    }

    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static Type[] Safe(Func<Type[]> f) { try { return f(); } catch { return []; } }

    private static MethodBase? FindMethod(Type? t, string name, int paramCount)
    {
        if (t == null) return null;
        try
        {
            var def = t.IsConstructedGenericType ? t.GetGenericTypeDefinition() : t;
            MethodBase? m = name == ".ctor"
                ? def.GetConstructors(All).FirstOrDefault(c => c.GetParameters().Length == paramCount)
                : def.GetMethods(All).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == paramCount);
            return m;
        }
        catch { return null; }
    }

    private Type? ParentType(EntityHandle parent) => parent.Kind switch
    {
        HandleKind.TypeDefinition => TypeOf((TypeDefinitionHandle)parent),
        HandleKind.TypeReference => TypeOf((TypeReferenceHandle)parent),
        HandleKind.TypeSpecification => _md.GetTypeSpecification((TypeSpecificationHandle)parent).DecodeSignature(Provider, null),
        _ => null,
    };

    private Type? TypeOf(TypeDefinitionHandle h)
    {
        var td = _md.GetTypeDefinition(h);
        var name = _md.GetString(td.Name);
        var decl = td.GetDeclaringType();
        if (!decl.IsNil) return TypeOf(decl)?.GetNestedType(name, All);
        return _hud.FindType(Full(_md.GetString(td.Namespace), name));
    }

    private Type? TypeOf(TypeReferenceHandle h)
    {
        var tr = _md.GetTypeReference(h);
        var name = _md.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference) return TypeOf((TypeReferenceHandle)tr.ResolutionScope)?.GetNestedType(name, All);
        return _hud.FindType(Full(_md.GetString(tr.Namespace), name));
    }

    private static string Full(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;

    private SigProvider? _provider;
    private SigProvider Provider => _provider ??= new SigProvider(this);

    /// <summary>Signature types -> MLC types (null when a type can't be found; callers tolerate it).</summary>
    private sealed class SigProvider(IlTokens owner) : ISignatureTypeProvider<Type?, object?>
    {
        public Type? GetPrimitiveType(PrimitiveTypeCode typeCode) => owner._hud.FindType("System." + typeCode switch
        {
            PrimitiveTypeCode.Int32 => "Int32", PrimitiveTypeCode.Int64 => "Int64", PrimitiveTypeCode.UInt32 => "UInt32", PrimitiveTypeCode.UInt64 => "UInt64",
            PrimitiveTypeCode.Int16 => "Int16", PrimitiveTypeCode.UInt16 => "UInt16", PrimitiveTypeCode.Byte => "Byte", PrimitiveTypeCode.SByte => "SByte",
            PrimitiveTypeCode.Boolean => "Boolean", PrimitiveTypeCode.Single => "Single", PrimitiveTypeCode.Double => "Double", PrimitiveTypeCode.Char => "Char",
            PrimitiveTypeCode.String => "String", PrimitiveTypeCode.Object => "Object", PrimitiveTypeCode.IntPtr => "IntPtr", PrimitiveTypeCode.UIntPtr => "UIntPtr",
            PrimitiveTypeCode.Void => "Void", _ => "Object",
        });
        public Type? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => owner.TypeOf(handle);
        public Type? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => owner.TypeOf(handle);
        public Type? GetTypeFromSpecification(MetadataReader reader, object? ctx, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, ctx);
        public Type? GetGenericInstantiation(Type? genericType, ImmutableArray<Type?> typeArguments)
        {
            if (genericType == null || typeArguments.Any(a => a == null)) return genericType;
            try { return genericType.MakeGenericType(typeArguments.Select(a => a!).ToArray()); } catch { return genericType; }
        }
        public Type? GetSZArrayType(Type? elementType) { try { return elementType?.MakeArrayType(); } catch { return null; } }
        public Type? GetArrayType(Type? elementType, ArrayShape shape) { try { return elementType?.MakeArrayType(shape.Rank); } catch { return null; } }
        public Type? GetByReferenceType(Type? elementType) { try { return elementType?.MakeByRefType(); } catch { return null; } }
        public Type? GetPointerType(Type? elementType) { try { return elementType?.MakePointerType(); } catch { return null; } }
        public Type? GetPinnedType(Type? elementType) => elementType;
        public Type? GetModifiedType(Type? modifier, Type? unmodifiedType, bool isRequired) => unmodifiedType;
        public Type? GetFunctionPointerType(MethodSignature<Type?> signature) => null;
        public Type? GetGenericMethodParameter(object? genericContext, int index) => null;
        public Type? GetGenericTypeParameter(object? genericContext, int index) => null;
    }
}
