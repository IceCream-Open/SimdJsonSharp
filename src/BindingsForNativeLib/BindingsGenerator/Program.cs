﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Linq;
using CppAst;

namespace BindingsGenerator
{
    public class Program
    {
        /// <summary>
        /// Types or arguments we don't want to bind for now
        /// </summary>
        private static string[] _typesToIgnore = { "iterator::scopeindex_t", "simdjson", "ParsedJson::InvalidJSON", "basic_ostream", "padded_string" };
        private static List<string> _allClassNames = new List<string>();
        public const string MainClassName = "SimdJsonN"; // for global functions
        public const string Namespace = "SimdJsonSharp";

        public static unsafe void Main(string[] args)
        {
            // input (single header)
            string headerPath = Path.Combine(Environment.CurrentDirectory, "../../src/BindingsForNativeLib/SimdJsonNative/simdjson.h");

            // output
            string cgluePath = Path.Combine(Environment.CurrentDirectory, "../../src/BindingsForNativeLib/SimdJsonNative/bindings.cpp");
            string bindingsPath = Path.Combine(Environment.CurrentDirectory, "../../src/BindingsForNativeLib/SimdJsonSharp.Bindings/Bindings.Generated.cs");


            var options = new CppParserOptions();
            options.ConfigureForWindowsMsvc(CppTargetCpu.X86_64);
            options.EnableMacros();
            options.AdditionalArguments.Add("-std=c++17");
            // TODO: test on macOS
            CppCompilation compilation = CppParser.ParseFile(headerPath, options);

            var csSb = new StringBuilder();
            csSb.AppendLine("// THIS FILE IS AUTOGENERATED!");
            csSb.AppendLine();
            csSb.AppendLine("using System;");
            csSb.AppendLine("using System.Runtime.InteropServices;");
            csSb.AppendLine();
            csSb.Append($"namespace {Namespace}\n{{");

            // Since we bind C++ we need to generate a DllImport-friendly C glue
            var cSb = new StringBuilder();
            cSb.AppendLine("// THIS FILE IS AUTOGENERATED!");
            cSb.AppendLine("#include \"simdjson.h\"");
            cSb.AppendLine();
            cSb.AppendLine("#if (defined WIN32 || defined _WIN32)");
            cSb.AppendLine("#define EXPORTS(returntype) extern \"C\" __declspec(dllexport) returntype __cdecl");
            cSb.AppendLine("#else");
            cSb.AppendLine("#define EXPORTS(returntype) extern \"C\" __attribute__((visibility(\"default\"))) returntype");
            cSb.AppendLine("#endif");

            List<CppClass> allClasses = compilation.GetAllClassesRecursively();
            _allClassNames = allClasses.Select(c => c.GetDisplayName()).ToList();

            foreach (CppClass cppClass in allClasses)
            {
                var csApiMethodsSb = new StringBuilder();
                var csDllImportsSb = new StringBuilder();

                string className = cppClass.Name;
                if (cppClass.Parent is CppClass parentClass)
                    className = $"{parentClass.Name}::{className}";

                if (_typesToIgnore.Contains(className))
                    continue;

                // prefix for all members
                string prefix = cppClass.Name + "_";

                cSb.AppendLine();
                cSb.AppendLine($"/* {className} */");


                IEnumerable<CppFunction> allFunctions = cppClass.Constructors.Take(1 /* HACK: take only first available ctors for now :) */).Concat(cppClass.Functions);
                foreach (CppFunction cppFunc in allFunctions)
                {
                    var argDefinitions = new List<string>();
                    var argInvocations = new List<string>();

                    // skip operators for now
                    if (cppFunc.IsOperator())
                        continue;

                    bool isStatic = cppFunc.StorageQualifier == CppStorageQualifier.Static;

                    string funcPrefix = prefix; // Class_function()
                    if (isStatic)
                        funcPrefix += "s_"; // Class_s_staticFunction()

                    if (!cppFunc.IsConstructor && !isStatic)
                        argDefinitions.Add($"{className}* target");

                    foreach (CppParameter cppParam in cppFunc.Parameters)
                    {
                        var type = cppParam.Type.GetDisplayName();

                        // method contains a parameter of type we don't support - skip the whole method (goto)
                        if (_typesToIgnore.Any(t => type.Contains(t)))
                            goto SKIP_FUNC;

                        string invocation = cppParam.Name;

                        // HACK: replace references with pointers (for classes)
                        if (type.EndsWith("&"))
                        {
                            type = type.Remove(type.Length - 1) + "*";
                            invocation = "*" + invocation;
                        }

                        argDefinitions.Add($"{type} {cppParam.Name}");
                        argInvocations.Add(invocation);
                    }

                    string funcReturnType = cppFunc.IsConstructor ? $"{className}*" : cppFunc.ReturnType.GetDisplayName();
                    string finalFuncName = $"{funcPrefix}{cppFunc.Name}";

                    // Func declaration
                    cSb.Append($"EXPORTS({funcReturnType}) {finalFuncName}({argDefinitions.AsCommaSeparatedStr()})");
                    cSb.Append(" {");

                    // Body
                    if (cppFunc.IsConstructor)
                    {
                        cSb.Append($" return new {className}({argInvocations.AsCommaSeparatedStr()});");
                    }
                    else
                    {
                        string returnStr = cppFunc.ReturnType.IsVoid() ? " " : " return ";
                        string invokeTarget = "target->";
                        if (isStatic)
                            invokeTarget = className + "::";

                        cSb.Append($"{returnStr}{invokeTarget}{cppFunc.Name}({argInvocations.AsCommaSeparatedStr()});");
                    }

                    cSb.Append(" }");
                    cSb.AppendLine();

                    var (dllImports, apiMethods) = GenerateMethodCSharp(cppFunc, finalFuncName);
                    csDllImportsSb.AppendLine(dllImports);
                    csApiMethodsSb.AppendLine(apiMethods);

                    SKIP_FUNC:
                    continue;
                }

                // destructor
                cSb.Append($"EXPORTS(void) {prefix}Dispose({className}* target) {{ delete target; }}");
                cSb.AppendLine();

                // generate C# for this method:
                csSb.AppendLine(GenerateClassCSharp(cppClass, csDllImportsSb.ToString(), csApiMethodsSb.ToString()));
            }

            csSb.Append("}"); // end of namespace

            File.WriteAllText(cgluePath, cSb.ToString());
            File.WriteAllText(bindingsPath, csSb.ToString());
        }

        /// <summary>
        /// Generate C# class declaration
        /// </summary>
        public static string GenerateClassCSharp(CppClass cppClass, string dllImports, string apis)
        {
            const string classTemplate = @"
    public unsafe partial class %ClassName% : IDisposable
    {
        /// <summary>
        /// Pointer to the underlying native object
        /// </summary>
        public void* Handle { get; private set; }

        /// <summary>
        /// Create %ClassName% from a native pointer
        /// </summary>
        public %ClassName%(void* handle) => this.Handle = handle;

%API%

#region DllImports
%DLLIMPORTS%
#endregion

        private static readonly object DisposeSync = new object();

        public void Dispose()
        {
            if (Handle != (void*) IntPtr.Zero)
            {
                lock (DisposeSync)
                {
                    if (Handle != (void*) IntPtr.Zero)
                    {
                        %ClassNameC%_Dispose(Handle);
                        Handle = (void*) IntPtr.Zero;
                    }
                }
            }
        }

        ~%ClassName%() => Dispose();

        [DllImport(%MainClassName%.NativeLib, CallingConvention = CallingConvention.Cdecl)]
        private static extern void %ClassNameC%_Dispose(void* target);
    }";

            return classTemplate
                .Replace("%ClassName%", RemapTypeToCSharp(cppClass.Name))
                .Replace("%ClassNameC%", cppClass.Name)
                .Replace("%MainClassName%", MainClassName)
                .Replace("%API%", apis)
                .Replace("%DLLIMPORTS%", dllImports);
        }

        /// <summary>
        /// <param name="function">Function we about to generate [DllImport] for</param>
        /// <param name="cname">Exact C name</param>
        /// </summary>
        public static (string, string) GenerateMethodCSharp(CppFunction function, string cname)
        {
            var dllImportSb = new StringBuilder();
            var apiDefinitionSb = new StringBuilder();
            var apiImplSb = new StringBuilder();
            bool castToBool = false; // for cases when we a method returns non-blittable bools
            bool isStatic = function.StorageQualifier == CppStorageQualifier.Static;

            List<CppParameter> parameters = function.Parameters;

            apiDefinitionSb.Append("public");
            if (isStatic)
                apiDefinitionSb.Append(" static");

            dllImportSb.Append(Tabs(2)).Append($"[DllImport({MainClassName}.NativeLib, CallingConvention=CallingConvention.Cdecl)]\n");
            dllImportSb.Append(Tabs(2)).Append("private static extern ");

            apiImplSb.Append(" => ");

            if (function.IsConstructor)
                dllImportSb.Append("void*");
            else
            {
                var returnTypeCs = RemapTypeToCSharp(function.ReturnType.GetDisplayName());
                var returnTypeApiCs = returnTypeCs;
                if (returnTypeCs == "nint") // HACK - I want bindings to be Any CPU and don't want to expose IntPtr.
                {
                    returnTypeApiCs = returnTypeCs = "long";
                    apiImplSb.Append("(long)"); // cast to long
                }

                if (returnTypeCs == "bool")
                {
                    // bool is not blittable
                    castToBool = true;
                    returnTypeCs = "byte";
                }

                dllImportSb.Append(returnTypeCs);
                apiDefinitionSb.Append(" ").Append(returnTypeApiCs);
            }

            dllImportSb.Append($" {cname}(");

            string apiName = ToCamelCase(function.Name);

            // ugly hacks
            if (isStatic && apiName == "IsObjectOrArray")
                apiName = "IsObjectOrArrayStatic";
            if (apiName == "GetType")
                apiName = "GetTokenType";

            if (function.IsConstructor)
            {
                if (function.Parent is CppClass cppClass)
                    apiName = RemapTypeToCSharp(cppClass.Name);
                else
                    apiName = MainClassName; // Class for global functions
            }

            apiDefinitionSb.Append($" {apiName}");

            bool convertedToProperty = false;

            if (function.Name.StartsWith("is") &&
                parameters.Count == 0 &&
                !function.IsConstructor)
                convertedToProperty = true;
            else
                apiDefinitionSb.Append("(");

            if (function.IsConstructor)
                apiImplSb.Append($"this.Handle = {cname}(");
            else
                apiImplSb.Append($"{cname}(");

            if (!function.IsConstructor && !isStatic)
            {
                dllImportSb.Append("void* target");
                apiImplSb.Append("this.Handle");

                if (parameters.Count > 0)
                {
                    dllImportSb.Append(", ");
                    apiImplSb.Append(", ");
                }
            }

            for (var i = 0; i < parameters.Count; i++)
            {
                CppParameter parameter = parameters[i];

                // TODO: extract default value if any

                bool isWrapper = false;
                var ctype = parameter.Type.GetDisplayName();
                if (_allClassNames.Any(c => ctype.Contains(c)))
                    isWrapper = true;

                var paramTypeNameCs = RemapTypeToCSharp(parameter.Type.GetDisplayName());
                var paramTypePinvokeCs = paramTypeNameCs;

                if (paramTypeNameCs == "nint")
                {
                    paramTypeNameCs = "long";
                    paramTypePinvokeCs = "IntPtr";
                    apiImplSb.Append("(IntPtr)"); // cast to IntPtr
                }


                dllImportSb.Append(isWrapper ? "void*" : paramTypePinvokeCs);
                dllImportSb.Append(" ");
                dllImportSb.Append(parameter.Name);

                apiDefinitionSb.Append(paramTypeNameCs);
                apiDefinitionSb.Append(" ");
                apiDefinitionSb.Append(parameter.Name);

                apiImplSb.Append(parameter.Name);
                if (isWrapper)
                    apiImplSb.Append(".Handle");

                if (i < parameters.Count - 1)
                {
                    dllImportSb.Append(", ");
                    apiDefinitionSb.Append(", ");
                    apiImplSb.Append(", ");
                }
            }

            dllImportSb.Append(");");
            if (!convertedToProperty)
                apiDefinitionSb.Append(")");

            if (castToBool)
                apiImplSb.AppendLine(") > 0;");
            else
                apiImplSb.AppendLine(");");

            return (dllImportSb.ToString(), $"{GenerateCommentsSummary(2, function.Comment)}{Tabs(2)}{apiDefinitionSb}{apiImplSb}");
        }

        static string Tabs(int tabs) => new string(' ', tabs * 4); // I am sorry, but spaces...

        /// <summary>
        /// Generate summary-style comments
        /// </summary>
        public static string GenerateCommentsSummary(int margin, string comments)
        {
            var sb = new StringBuilder();
            sb.Append(Tabs(margin)).AppendLine("/// <summary>");

            if (!string.IsNullOrWhiteSpace(comments))
                foreach (var commentLine in comments.Split('\n'))
                    sb.Append(Tabs(margin)).Append("/// ").AppendLine(commentLine);

            sb.Append(Tabs(margin)).AppendLine("/// </summary>");
            return sb.ToString();
        }

        /// <summary>
        /// "snake_case" to "CamelCase"
        /// </summary>
        public static string ToCamelCase(string str) =>
            string.Join("", str.Split("_", StringSplitOptions.RemoveEmptyEntries)
                .Select(b => char.ToUpperInvariant(b[0]) + b.Substring(1)));

        /// <summary>
        /// Remap native type name to C#
        /// </summary>
        public static string RemapTypeToCSharp(string type)
        {
            if (type.EndsWith("*"))
                return RemapTypeToCSharp(type.Remove(type.Length - 1)) + "*";

            if (type.EndsWith("&"))
                type = type.Substring(0, type.Length - 1);

            switch (type)
            {
                case "uint8_t": return "byte";
                case "uint16_t": return "ushort";
                case "uint32_t": return "uint";
                case "uint64_t": return "ulong";

                case "int8_t": return "sbyte";
                case "int16_t": return "short";
                case "int32_t": return "int";
                case "int64_t": return "long";

                case "size_t": return "nint"; // IntPtr ?

                case "const char": return "sbyte";
                case "char": return "sbyte";

                case "iterator": return "ParsedJsonIteratorN";
                case "ParsedJson": return "ParsedJsonN";
            }

            return type;
        }
    }

    public static class CppAstExtensions
    {
        public static bool IsVoid(this CppType type) => type.GetDisplayName() == "void";
        public static bool IsOperator(this CppFunction func) => func.Name.StartsWith("operator"); // TODO: regex?

        public static List<CppClass> GetAllClassesRecursively(this CppCompilation compilation)
        {
            var cppClasses = new List<CppClass>();
            foreach (var cppClass in compilation.Classes)
                VisitClass(cppClasses, cppClass);
            return cppClasses;
        }

        private static void VisitClass(List<CppClass> cppClasses, CppClass cppClass)
        {
            cppClasses.Add(cppClass);
            foreach (var subClass in cppClass.Classes)
                VisitClass(cppClasses, subClass);
        }

        public static string AsCommaSeparatedStr(this IEnumerable<object> obj) => string.Join(", ", obj);
    }
}