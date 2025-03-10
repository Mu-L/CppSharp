using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using CppSharp.AST;
using CppSharp.AST.Extensions;
using CppSharp.Generators;
using CppSharp.Generators.C;
using CppSharp.Generators.CSharp;
using CppSharp.Parser;
using CppSharp.Passes;
using static CppSharp.CodeGeneratorHelpers;

namespace CppSharp
{
    /// <summary>
    /// Generates parser bootstrap code.
    /// </summary>
    internal class Bootstrap : ILibrary
    {
        private static bool CreatePatch = true;
        private static string OutputPath = CreatePatch ? "BootstrapPatch" : "";

        private static string GetSourceDirectory(string dir)
        {
            var directory = Directory.GetParent(Directory.GetCurrentDirectory());

            while (directory != null)
            {
                var path = Path.Combine(directory.FullName, dir);

                if (Directory.Exists(path))
                    return path;

                directory = directory.Parent;
            }

            throw new Exception("Could not find build directory: " + dir);
        }

        private static string GetLLVMRevision(string llvmDir)
            => File.ReadAllText(Path.Combine(llvmDir, "LLVM-commit"));

        private static string GetLLVMBuildDirectory()
        {
            var llvmDir = Path.Combine(GetSourceDirectory("build"), "llvm");
            var llvmRevision = GetLLVMRevision(llvmDir).Substring(0, 6);

            return Directory.EnumerateDirectories(llvmDir, $"*{llvmRevision}*-Rel*").FirstOrDefault() ??
                Directory.EnumerateDirectories(llvmDir, $"*{llvmRevision}*").FirstOrDefault();
        }

        public void Setup(Driver driver)
        {
            driver.Options.GeneratorKind = GeneratorKind.CSharp;
            driver.Options.DryRun = true;
            driver.ParserOptions.EnableRTTI = true;
            driver.ParserOptions.LanguageVersion = LanguageVersion.CPP17_GNU;
            driver.ParserOptions.SkipLayoutInfo = true;
            driver.ParserOptions.UnityBuild = true;

            var module = driver.Options.AddModule("CppSharp");

            module.Defines.Add("__STDC_LIMIT_MACROS");
            module.Defines.Add("__STDC_CONSTANT_MACROS");

            var llvmPath = GetLLVMBuildDirectory();

            if (llvmPath == null)
                throw new Exception("Could not find LLVM build directory");

            module.IncludeDirs.AddRange(new[]
            {
                Path.Combine(llvmPath, "llvm", "include"),
                Path.Combine(llvmPath, "build", "include"),
                Path.Combine(llvmPath, "build", "clang", "include"),
                Path.Combine(llvmPath, "clang", "include")
            });

            module.Headers.AddRange(new[]
            {
                "clang/AST/Stmt.h",
                "clang/AST/StmtCXX.h",
                "clang/AST/Expr.h",
                "clang/AST/ExprCXX.h",
            });

            module.LibraryDirs.Add(Path.Combine(llvmPath, "lib"));
        }

        public void SetupPasses(Driver driver)
        {
        }

        public void Preprocess(Driver driver, ASTContext ctx)
        {
            new IgnoreMethodsWithParametersPass { Context = driver.Context }
                .VisitASTContext(ctx);
            new GetterSetterToPropertyPass { Context = driver.Context }
                .VisitASTContext(ctx);
            new CheckEnumsPass { Context = driver.Context }
                .VisitASTContext(ctx);

            var preprocessDecls = new PreprocessDeclarations();
            foreach (var unit in ctx.TranslationUnits)
                unit.Visit(preprocessDecls);

            var exprUnit = ctx.TranslationUnits.Find(unit =>
                unit.FileName.Contains("Expr.h"));
            var exprCxxUnit = ctx.TranslationUnits.Find(unit =>
                unit.FileName.Contains("ExprCXX.h"));

            var exprClass = exprUnit.FindNamespace("clang").FindClass("Expr");
            var exprSubclassVisitor = new SubclassVisitor(exprClass);
            exprUnit.Visit(exprSubclassVisitor);
            exprCxxUnit.Visit(exprSubclassVisitor);
            ExprClasses = exprSubclassVisitor.Classes;

            CodeGeneratorHelpers.CppTypePrinter = new CppTypePrinter(driver.Context);
            CodeGeneratorHelpers.CppTypePrinter.PushScope(TypePrintScopeKind.Local);

            GenerateStmt(driver.Context);
            GenerateExpr(driver.Context);
        }

        public void Postprocess(Driver driver, ASTContext ctx)
        {
        }

        private IEnumerable<Class> ExprClasses;

        private void GenerateExpr(BindingContext ctx)
        {
            var operationKindsUnit = ctx.ASTContext.TranslationUnits.Find(unit =>
                unit.FileName.Contains("OperationKinds.h"));
            var operatorKindsUnit = ctx.ASTContext.TranslationUnits.Find(unit =>
                unit.FileName.Contains("OperatorKinds.h"));
            var dependenceFlagsUnit = ctx.ASTContext.TranslationUnits.Find(unit =>
                unit.FileName.Contains("DependenceFlags.h"));
            var exprDependence = dependenceFlagsUnit
                .FindNamespace("clang")
                .FindClass("ExprDependenceScope")
                .FindEnum("ExprDependence");

            // Move to outer namespace
            exprDependence.Namespace = exprDependence.Namespace.Namespace.Namespace;

            var typeTraitsUnit = ctx.ASTContext.TranslationUnits.Find(unit =>
                unit.FileName == "TypeTraits.h");
            var unaryExprOrTypeTrait = typeTraitsUnit
                .FindEnum("clang::UnaryExprOrTypeTrait");

            var specifiersUnit = ctx.ASTContext.TranslationUnits.Find(unit =>
                unit.FileName == "Specifiers.h");
            var nonOdrUseReason = specifiersUnit.FindEnum("clang::NonOdrUseReason");

            var apFloatUnit = ctx.ASTContext.TranslationUnits.Find(unit =>
                unit.FileName == "APFloat.h");
            var floatSemantics = apFloatUnit
                .FindNamespace("llvm")
                .FindClass("APFloatBase")
                .FindEnum("Semantics");
            floatSemantics.Name = "FloatSemantics";
            // Move to outer namespace
            floatSemantics.Namespace = floatSemantics.Namespace.Namespace.Namespace;

            var decls = new Declaration[] {
                    operationKindsUnit, operatorKindsUnit,
                    unaryExprOrTypeTrait, nonOdrUseReason,
                    exprDependence, floatSemantics
                }.Union(ExprClasses);

            // Write the native declarations headers
            var declsCodeGen = new ExprDeclarationsCodeGenerator(ctx, decls);
            declsCodeGen.GenerateDeclarations();
            WriteFile(declsCodeGen, Path.Combine(OutputPath, "CppParser", "Expr.h"));

            var defsCodeGen = new ExprDefinitionsCodeGenerator(ctx, decls);
            defsCodeGen.GenerateDefinitions();
            WriteFile(defsCodeGen, Path.Combine(OutputPath, "CppParser", "Expr.cpp"));

            // Write the native parsing routines
            var parserCodeGen = new ExprParserCodeGenerator(ctx, decls);
            parserCodeGen.GenerateParser();
            WriteFile(parserCodeGen, Path.Combine(OutputPath, "CppParser", "ParseExpr.cpp"));

            // Write the managed declarations
            var managedCodeGen = new ManagedParserCodeGenerator(ctx, decls);
            managedCodeGen.GenerateDeclarations();
            WriteFile(managedCodeGen, Path.Combine(OutputPath, "AST", "Expr.cs"));

            managedCodeGen = new ExprASTConverterCodeGenerator(ctx, decls);
            managedCodeGen.Process();
            WriteFile(managedCodeGen, Path.Combine(OutputPath, "Parser", "ASTConverter.Expr.cs"));
        }

        private void GenerateStmt(BindingContext ctx)
        {
            var stmtUnit = ctx.ASTContext.TranslationUnits.Find(unit =>
                unit.FileName.Contains("Stmt.h"));
            var stmtCxxUnit = ctx.ASTContext.TranslationUnits.Find(unit =>
                unit.FileName.Contains("StmtCXX.h"));

            var stmtClass = stmtUnit.FindNamespace("clang").FindClass("Stmt");

            var stmtClassEnum = stmtClass.FindEnum("StmtClass");
            stmtClass.Declarations.Remove(stmtClassEnum);
            CleanupEnumItems(stmtClassEnum);

            var stmtSubclassVisitor = new SubclassVisitor(stmtClass);
            stmtUnit.Visit(stmtSubclassVisitor);
            stmtCxxUnit.Visit(stmtSubclassVisitor);

            var specifiersUnit = ctx.ASTContext.TranslationUnits.Find(unit =>
                unit.FileName == "Specifiers.h");
            var ifStatementKind = specifiersUnit.FindNamespace("clang").FindEnum("IfStatementKind");

            var decls = new Declaration[]
                {
                    stmtClassEnum, ifStatementKind
                }
                .Union(stmtSubclassVisitor.Classes);

            // Write the native declarations headers
            var declsCodeGen = new StmtDeclarationsCodeGenerator(ctx, decls);
            declsCodeGen.GenerateDeclarations();
            WriteFile(declsCodeGen, Path.Combine(OutputPath, "CppParser", "Stmt.h"));

            var stmtClasses = stmtSubclassVisitor.Classes;
            var defsCodeGen = new StmtDefinitionsCodeGenerator(ctx, stmtClasses);
            defsCodeGen.GenerateDefinitions();
            WriteFile(defsCodeGen, Path.Combine(OutputPath, "CppParser", "Stmt.cpp"));

            // Write the native parsing routines
            var parserCodeGen = new StmtParserCodeGenerator(ctx, stmtClasses, ExprClasses);
            parserCodeGen.GenerateParser();
            WriteFile(parserCodeGen, Path.Combine(OutputPath, "CppParser", "ParseStmt.cpp"));

            // Write the managed declarations
            var managedCodeGen = new ManagedParserCodeGenerator(ctx, decls);
            managedCodeGen.GenerateDeclarations();
            WriteFile(managedCodeGen, Path.Combine(OutputPath, "AST", "Stmt.cs"));

            managedCodeGen = new ManagedVisitorCodeGenerator(ctx, decls.Union(ExprClasses));
            managedCodeGen.Process();
            WriteFile(managedCodeGen, Path.Combine(OutputPath, "AST", "StmtVisitor.cs"));

            managedCodeGen = new StmtASTConverterCodeGenerator(ctx, decls, stmtClassEnum);
            managedCodeGen.Process();
            WriteFile(managedCodeGen, Path.Combine(OutputPath, "Parser", "ASTConverter.Stmt.cs"));
        }

        private static void CleanupEnumItems(Enumeration exprClassEnum)
        {
            foreach (var item in exprClassEnum.Items)
            {
                if (item.Name.StartsWith("first", StringComparison.InvariantCulture) ||
                    item.Name.StartsWith("last", StringComparison.InvariantCulture))
                    item.ExplicitlyIgnore();

                if (item.Name.StartsWith("OMP") || item.Name.StartsWith("ObjC"))
                    item.ExplicitlyIgnore();

                item.Name = RemoveFromEnd(item.Name, "Class");
            }
        }

        private static Stream GenerateStreamFromString(string s)
        {
            var stream = new MemoryStream();
            var writer = new StreamWriter(stream);
            writer.Write(s);
            writer.Flush();
            stream.Position = 0;
            return stream;
        }

        private static string CalculateMD5(string text)
        {
            using var md5 = MD5.Create();
            using var stream = GenerateStreamFromString(text);

            var hash = md5.ComputeHash(stream);
            return BitConverter.ToString(hash)
                .Replace("-", "")
                .ToLowerInvariant();
        }

        private static bool WriteFile(CodeGenerator codeGenerator, string basePath)
        {
            var srcDir = GetSourceDirectory("src");
            var path = Path.Combine(srcDir, basePath);

            string oldHash = string.Empty;
            if (File.Exists(path))
                oldHash = CalculateMD5(File.ReadAllText(path));

            var sourceCode = codeGenerator.Generate();
            var newHash = CalculateMD5(sourceCode);

            if (oldHash == newHash)
                return false;

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, sourceCode);
            Console.WriteLine($"Writing '{Path.GetFileName(path)}'.");
            return true;
        }

        public static void Main(string[] args)
        {
            Console.WriteLine("Generating parser bootstrap code...");
            ConsoleDriver.Run(new Bootstrap());
            Console.WriteLine();
        }
    }

    internal class PreprocessDeclarations : AstVisitor
    {
        private static void Check(Declaration decl)
        {
            if (string.IsNullOrWhiteSpace(decl.Name))
            {
                decl.ExplicitlyIgnore();
                return;
            }

            if (decl.Name.EndsWith("Bitfields", StringComparison.Ordinal))
                decl.ExplicitlyIgnore();

            if (decl.Name.EndsWith("Iterator", StringComparison.Ordinal))
                decl.ExplicitlyIgnore();

            if (decl.Name is "AssociationTy" or "AssociationIteratorTy")
                decl.ExplicitlyIgnore();

            if (decl.Name == "EmptyShell")
                decl.ExplicitlyIgnore();

            if (decl.Name == "DecomposedForm")
                decl.ExplicitlyIgnore();

            if (decl.Name is "APIntStorage" or "APFloatStorage")
                decl.ExplicitlyIgnore();
        }

        public override bool VisitClassDecl(Class @class)
        {
            //
            // Statements
            //

            if (CodeGeneratorHelpers.IsAbstractStmt(@class))
                @class.IsAbstract = true;

            Check(@class);

            foreach (var @base in @class.Bases)
            {
                if (@base.Class == null)
                    continue;

                if (@base.Class.Name.Contains("TrailingObjects"))
                    @base.ExplicitlyIgnore();

                if (@base.Class.Name == "APIntStorage")
                {
                    @base.ExplicitlyIgnore();

                    var property = new Property
                    {
                        Access = AccessSpecifier.Public,
                        Name = "value",
                        Namespace = @class,
                        QualifiedType = new QualifiedType(
                            new BuiltinType(PrimitiveType.ULongLong))
                    };

                    if (!@class.Properties.Exists(p => p.Name == property.Name))
                        @class.Properties.Add(property);
                }

                else if (@base.Class.Name == "APFloatStorage")
                {
                    @base.ExplicitlyIgnore();

                    var property = new Property
                    {
                        Access = AccessSpecifier.Public,
                        Name = "value",
                        Namespace = @class,
                        QualifiedType = new QualifiedType(
                            new BuiltinType(PrimitiveType.LongDouble))
                    };

                    if (!@class.Properties.Exists(p => p.Name == property.Name))
                        @class.Properties.Add(property);
                }
            }

            //
            // Expressions
            //

            if (@class.Name.EndsWith("EvalStatus") || @class.Name.EndsWith("EvalResult"))
                @class.ExplicitlyIgnore();

            if (@class.Name == "Expr")
            {
                foreach (var property in @class.Properties)
                {
                    switch (property.Name)
                    {
                        case "isObjCSelfExpr":
                        case "refersToVectorElement":
                        case "refersToGlobalRegisterVar":
                        case "isKnownToHaveBooleanValue":
                        case "isDefaultArgument":
                        case "isImplicitCXXThis":
                        case "bestDynamicClassTypeExpr":
                        case "refersToBitField":
                            property.ExplicitlyIgnore();
                            break;
                    }
                }
            }

            return base.VisitClassDecl(@class);
        }

        public override bool VisitClassTemplateDecl(ClassTemplate template)
        {
            Check(template);
            return base.VisitClassTemplateDecl(template);
        }

        public override bool VisitTypeAliasTemplateDecl(TypeAliasTemplate template)
        {
            Check(template);
            return base.VisitTypeAliasTemplateDecl(template);
        }

        public override bool VisitProperty(Property property)
        {
            if (property.Name == "stripLabelLikeStatements")
                property.ExplicitlyIgnore();

            return base.VisitProperty(property);
        }

        public override bool VisitEnumDecl(Enumeration @enum)
        {
            if (AlreadyVisited(@enum))
                return false;

            if (@enum.Name == "APFloatSemantics")
                @enum.ExplicitlyIgnore();

            if (@enum.IsAnonymous || string.IsNullOrWhiteSpace(@enum.Name))
                @enum.ExplicitlyIgnore();

            @enum.SetScoped();

            RemoveEnumItemsPrefix(@enum);

            return base.VisitEnumDecl(@enum);
        }

        private void RemoveEnumItemsPrefix(Enumeration @enum)
        {
            var enumItem = @enum.Items.FirstOrDefault();
            if (enumItem == null)
                return;

            var underscoreIndex = enumItem.Name.IndexOf('_');
            if (underscoreIndex == -1)
                return;

            if (enumItem.Name[underscoreIndex + 1] == '_')
                underscoreIndex++;

            var prefix = enumItem.Name.Substring(0, ++underscoreIndex);
            if (@enum.Items.Count(item => item.Name.StartsWith(prefix)) < 3)
                return;

            foreach (var item in @enum.Items)
            {
                if (!item.Name.StartsWith(prefix))
                {
                    item.ExplicitlyIgnore();
                    continue;
                }

                item.Name = item.Name.Substring(prefix.Length);
                item.Name = CaseRenamePass.ConvertCaseString(item,
                    RenameCasePattern.UpperCamelCase);
            }
        }
    }

    internal class SubclassVisitor : AstVisitor
    {
        public HashSet<Class> Classes;
        private readonly Class @class;

        public SubclassVisitor(Class @class)
        {
            this.@class = @class;
            Classes = new HashSet<Class>();
        }

        private static bool IsDerivedFrom(Class subclass, Class superclass)
        {
            while (subclass != null)
            {
                if (subclass == superclass)
                    return true;

                if (!subclass.HasBaseClass)
                    return false;

                subclass = subclass.BaseClass;
            }

            return false;
        }

        public override bool VisitClassDecl(Class @class)
        {
            if (!VisitDeclaration(@class))
                return false;

            if (!@class.IsIncomplete && IsDerivedFrom(@class, this.@class))
                Classes.Add(@class);

            return base.VisitClassDecl(@class);
        }
    }

    #region Managed code generators

    internal class ManagedParserCodeGenerator : CSharpSources
    {
        internal readonly IEnumerable<Declaration> Declarations;

        public ManagedParserCodeGenerator(BindingContext context,
            IEnumerable<Declaration> declarations)
            : base(context)
        {
            Declarations = declarations;
            TypePrinter.PushScope(TypePrintScopeKind.Local);
            TypePrinter.PrintModuleOutputNamespace = false;
        }

        public override void Process()
        {
            GenerateFilePreamble(CommentKind.BCPL);
            NewLine();
        }

        public void GenerateDeclarations()
        {
            Process();

            GenerateUsings();
            NewLine();

            WriteLine("namespace CppSharp.AST");
            WriteOpenBraceAndIndent();

            foreach (var decl in Declarations)
            {
                PushBlock();
                decl.Visit(this);
                PopBlock(NewLineKind.BeforeNextBlock);
            }

            UnindentAndWriteCloseBrace();
        }

        public override bool VisitNamespace(Namespace @namespace)
        {
            return base.VisitDeclContext(@namespace);
        }

        public override bool VisitClassDecl(Class @class)
        {
            if (!@class.IsGenerated)
                return false;

            GenerateClassSpecifier(@class);
            NewLine();

            WriteOpenBraceAndIndent();

            PushBlock();
            VisitDeclContext(@class);
            PopBlock(NewLineKind.Always);

            WriteLine($"public {@class.Name}()");
            WriteOpenBraceAndIndent();
            UnindentAndWriteCloseBrace();
            NewLine();

            foreach (var method in @class.Methods)
            {
                if (SkipMethod(method))
                    continue;

                var iteratorType = GetIteratorType(method);
                var iteratorTypeName = GetIteratorTypeName(iteratorType, TypePrinter);
                var declName = GetDeclName(method, GeneratorKind.CSharp);

                WriteLine($@"public List<{iteratorTypeName}> {declName} {{ get; private set; }} = new List<{iteratorTypeName}>();");
            }

            foreach (var property in @class.Properties)
            {
                if (SkipProperty(property))
                    continue;

                string typeName = RemoveClangNamespacePrefix(GetDeclTypeName(
                    property.Type, TypePrinter));
                string propertyName = GetDeclName(property, GeneratorKind.CSharp);

                WriteLine($"public {typeName} {propertyName} {{ get; set; }}");
            }

            var rootBase = @class.GetNonIgnoredRootBase();
            var isStmt = rootBase != null && rootBase.Name == "Stmt";

            if (isStmt && !(@class.IsAbstract && @class.Name != "Stmt"))
            {
                NewLine();
                GenerateVisitMethod(@class);
            }

            UnindentAndWriteCloseBrace();

            return true;
        }

        private void GenerateVisitMethod(Class @class)
        {
            if (@class.IsAbstract)
            {
                WriteLine("public abstract T Visit<T>(IStmtVisitor<T> visitor);");
                return;
            }

            WriteLine("public override T Visit<T>(IStmtVisitor<T> visitor) =>");
            WriteLineIndent("visitor.Visit{0}(this);", @class.Name);
        }

        public override string GetBaseClassTypeName(BaseClassSpecifier @base)
        {
            var type = base.GetBaseClassTypeName(@base);
            return RemoveClangNamespacePrefix(type);
        }

        private static string RemoveClangNamespacePrefix(string type)
        {
            return type.StartsWith("clang.") ?
                type.Substring("clang.".Length) : type;
        }

        public override void GenerateUsings()
        {
            WriteLine("using System;");
            WriteLine("using System.Collections.Generic;");
        }

        public override void GenerateDeclarationCommon(Declaration decl)
        {
        }

        public override void GenerateNamespaceFunctionsAndVariables(
            DeclarationContext context)
        {

        }
    }

    internal class ManagedVisitorCodeGenerator : ManagedParserCodeGenerator
    {
        public ManagedVisitorCodeGenerator(BindingContext context,
            IEnumerable<Declaration> declarations)
            : base(context, declarations)
        {
        }

        public override void Process()
        {
            GenerateFilePreamble(CommentKind.BCPL);
            NewLine();

            WriteLine("namespace CppSharp.AST");
            WriteOpenBraceAndIndent();

            GenerateVisitor();
            NewLine();

            GenerateVisitorInterface();

            UnindentAndWriteCloseBrace();
        }

        private void GenerateVisitor()
        {
            WriteLine($"public abstract partial class AstVisitor");
            WriteOpenBraceAndIndent();

            foreach (var @class in Declarations.OfType<Class>())
            {
                if (@class.Name == "Stmt") continue;

                PushBlock();
                var paramName = "stmt";
                WriteLine("public virtual bool Visit{0}({0} {1})",
                    @class.Name, paramName);
                WriteOpenBraceAndIndent();

                WriteLine($"if (!Visit{@class.BaseClass.Name}({paramName}))");
                WriteLineIndent("return false;");
                NewLine();

                WriteLine("return true;");

                UnindentAndWriteCloseBrace();
                PopBlock(NewLineKind.BeforeNextBlock);
            }

            UnindentAndWriteCloseBrace();
        }

        private void GenerateVisitorInterface()
        {
            WriteLine($"public interface IStmtVisitor<out T>");
            WriteOpenBraceAndIndent();

            foreach (var @class in Declarations.OfType<Class>())
            {
                var paramName = "stmt";
                WriteLine("T Visit{0}({0} {1});",
                    @class.Name, paramName);
            }

            UnindentAndWriteCloseBrace();
        }
    }

    internal abstract class ASTConverterCodeGenerator : ManagedParserCodeGenerator
    {
        public abstract string BaseTypeName { get; }
        public string ParamName => BaseTypeName.ToLowerInvariant();

        public abstract bool IsAbstractASTNode(Class kind);

        protected ASTConverterCodeGenerator(BindingContext context, IEnumerable<Declaration> declarations)
            : base(context, declarations)
        {
        }

        public override void Process()
        {
            GenerateFilePreamble(CommentKind.BCPL);
            NewLine();

            WriteLine("using CppSharp.Parser.AST;");
            NewLine();
            WriteLine("using static CppSharp.ConversionUtils;");
            NewLine();

            WriteLine("namespace CppSharp");
            WriteOpenBraceAndIndent();

            GenerateVisitor();
            NewLine();

            GenerateConverter();

            UnindentAndWriteCloseBrace();
        }

        protected abstract void GenerateVisitorSwitch(IEnumerable<string> classes);

        protected virtual void GenerateVisitor()
        {
            var comment = new RawComment
            {
                BriefText = $"Implements the visitor pattern for the generated {ParamName} bindings.\n"
            };

            GenerateComment(comment);

            WriteLine($"public abstract class {BaseTypeName}Visitor<TRet>");
            WriteLineIndent("where TRet : class");
            WriteOpenBraceAndIndent();

            var classes = Declarations
                .OfType<Class>()
                .Where(@class => !IsAbstractASTNode(@class))
                .Select(@class => @class.Name)
                .ToArray();

            foreach (var className in classes)
                WriteLine("public abstract TRet Visit{0}({0} {1});", className, ParamName);

            NewLine();
            WriteLine($"public virtual TRet Visit(Parser.AST.{BaseTypeName} {ParamName})");
            WriteOpenBraceAndIndent();

            WriteLine($"if ({ParamName} == null)");
            WriteLineIndent("return default(TRet);");
            NewLine();

            GenerateVisitorSwitch(classes);

            UnindentAndWriteCloseBrace();
            UnindentAndWriteCloseBrace();
        }

        private void GenerateConverter()
        {
            WriteLine("public unsafe class {0}Converter : {0}Visitor<AST.{0}>", BaseTypeName);
            WriteOpenBraceAndIndent();

            foreach (var @class in Declarations.OfType<Class>())
            {
                if (IsAbstractASTNode(@class))
                    continue;

                PushBlock();
                WriteLine("public override AST.{0} Visit{1}({1} {2})", BaseTypeName, @class.Name, ParamName);
                WriteOpenBraceAndIndent();

                var qualifiedName = $"{GetQualifiedName(@class)}";
                WriteLine($"var _{ParamName} = new AST.{qualifiedName}();");

                var classHierarchy = GetBaseClasses(@class);
                foreach (var baseClass in classHierarchy)
                    GenerateMembers(baseClass);

                WriteLine($"return _{ParamName};");

                UnindentAndWriteCloseBrace();
                PopBlock(NewLineKind.BeforeNextBlock);
            }

            UnindentAndWriteCloseBrace();
        }

        private void GenerateMembers(Class @class)
        {
            foreach (var property in @class.Properties.Where(p => p.IsGenerated))
            {
                if (SkipProperty(property))
                    continue;

                property.Visit(this);
            }

            foreach (var method in @class.Methods)
            {
                if (SkipMethod(method))
                    continue;

                method.Visit(this);
            }
        }

        public override bool VisitProperty(Property property)
        {
            var propertyName = GetDeclName(property, GeneratorKind.CSharp);
            Write($"_{ParamName}.{propertyName} = ");

            var bindingsProperty = $"{ParamName}.{propertyName}";

            var type = property.Type;
            var declTypeName = GetDeclTypeName(type, TypePrinter);
            MarshalDecl(type, declTypeName, bindingsProperty);
            WriteLine(";");

            return true;
        }

        public override bool VisitMethodDecl(Method method)
        {
            var managedName = GetDeclName(method, GeneratorKind.CSharp);
            var nativeName = CaseRenamePass.ConvertCaseString(method, RenameCasePattern.LowerCamelCase);

            WriteLine($"for (uint i = 0; i < {ParamName}.Get{nativeName}Count; i++)");
            WriteOpenBraceAndIndent();
            WriteLine($"var _E = {ParamName}.Get{nativeName}(i);");

            var bindingsType = GetIteratorType(method);
            var iteratorTypeName = GetIteratorTypeName(bindingsType, TypePrinter);

            Write($"_{ParamName}.{managedName}.Add(");
            MarshalDecl(bindingsType, iteratorTypeName, "_E");
            WriteLine(");");

            UnindentAndWriteCloseBrace();

            return true;
        }

        protected virtual void MarshalDecl(AST.Type type, string declTypeName, string bindingsName)
        {
            var typeName = $"AST.{declTypeName}";
            if (type.TryGetEnum(out Enumeration @enum))
                Write($"(AST.{GetQualifiedName(@enum, TypePrinter)}) {bindingsName}");
            else if (typeName.Contains("SourceLocation"))
                Write($"VisitSourceLocation({bindingsName})");
            else if (typeName.Contains("SourceRange"))
                Write($"VisitSourceRange({bindingsName})");
            else if (typeName.Contains("Stmt"))
                Write($"VisitStatement({bindingsName}) as {typeName}");
            else if (typeName.Contains("Expr"))
                Write($"VisitExpression({bindingsName}) as {typeName}");
            else if (typeName.Contains("Decl") || typeName.Contains("Function") ||
                     typeName.Contains("Method") || typeName.Contains("Field"))
                Write($"VisitDeclaration({bindingsName}) as {typeName}");
            else if (typeName.Contains("QualifiedType"))
                Write($"VisitQualifiedType({bindingsName})");
            else if (typeName.Contains("TemplateArgument"))
                Write($"VisitTemplateArgument({bindingsName})");
            else
                Write($"{bindingsName}");
        }
    }

    #endregion

    #region Native code generators

    internal class NativeParserCodeGenerator : Generators.C.CCodeGenerator
    {
        internal readonly IEnumerable<Declaration> Declarations;

        public NativeParserCodeGenerator(BindingContext context, IEnumerable<Declaration> declarations)
            : base(context)
        {
            Declarations = declarations;
        }

        public override string FileExtension => throw new NotImplementedException();

        public virtual bool GeneratePragmaOnce => true;

        public override void Process()
        {
            Context.Options.GeneratorKind = GeneratorKind.CPlusPlus;
            CTypePrinter.PushScope(TypePrintScopeKind.Local);

            GenerateFilePreamble(CommentKind.BCPL);
            NewLine();

            if (GeneratePragmaOnce)
                WriteLine("#pragma once");

            NewLine();
        }

        public void GenerateCommonIncludes()
        {
            WriteInclude("Sources.h", CInclude.IncludeKind.Quoted);
        }

        public override List<string> GenerateExtraClassSpecifiers(Class @class) => new() { "CS_API" };

        public override bool VisitTypedefNameDecl(TypedefNameDecl typedef)
        {
            return true;
        }

        public override bool VisitTypedefDecl(TypedefDecl typedef)
        {
            return true;
        }

        public override bool VisitFunctionDecl(Function function)
        {
            return true;
        }

        public bool IsInheritedClass(Class @class)
        {
            return Declarations
                .OfType<Class>()
                .Select(decl => decl.Bases)
                .Any(bases =>
                    bases.Any(@base => @base.IsClass && @base.Class == @class)
                );
        }
    }

    internal abstract class DefinitionsCodeGenerator : NativeParserCodeGenerator
    {
        protected DefinitionsCodeGenerator(BindingContext context, IEnumerable<Declaration> declarations)
            : base(context, declarations)
        {
        }

        public abstract string BaseTypeName { get; }
        public string ParamName => BaseTypeName.ToLowerInvariant();

        public override bool GeneratePragmaOnce => false;

        public virtual void GenerateDefinitions()
        {
            Process();

            GenerateIncludes();
            NewLine();

            WriteLine("namespace CppSharp::CppParser::AST {");
            NewLine();

            foreach (var decl in Declarations.OfType<Class>())
                decl.Visit(this);

            WriteLine("}");
        }

        public virtual void GenerateIncludes()
        {
            GenerateCommonIncludes();
        }

        protected virtual string GenerateInit(Property property)
        {
            if (property.Type.IsPointer())
                return "nullptr";

            if (property.Type.IsPrimitiveType(PrimitiveType.Bool))
                return "false";

            var typeName = GetDeclTypeName(property);
            if (property.Type.TryGetClass(out Class _))
                return $"{typeName}()";

            if (property.Type.TryGetEnum(out Enumeration @enum))
                return $"{GetQualifiedName(@enum)}::{@enum.Items.First().Name}";

            return "0";
        }

        protected virtual void GenerateMemberInits(Class @class)
        {
            foreach (var property in @class.Properties)
            {
                if (SkipProperty(property))
                    continue;

                var typeName = GetDeclTypeName(property);
                if (typeName == "std::string")
                    continue;

                WriteLineIndent($", {GetDeclName(property)}({GenerateInit(property)})");
            }
        }

        public override bool VisitEnumDecl(Enumeration @enum)
        {
            return true;
        }
    }

    internal abstract class DeclarationsCodeGenerator : NativeParserCodeGenerator
    {
        protected DeclarationsCodeGenerator(BindingContext context, IEnumerable<Declaration> declarations)
            : base(context, declarations)
        {
        }

        public abstract string BaseTypeName { get; }
        public string ParamName => BaseTypeName.ToLowerInvariant();

        public virtual void GenerateDeclarations()
        {
            Process();
            GenerateIncludes();
            NewLine();

            WriteLine("namespace CppSharp::CppParser::AST {");
            NewLine();

            GenerateForwardDecls();
            NewLine();

            foreach (var decl in Declarations)
                decl.Visit(this);

            NewLine();
            WriteLine("}");
        }

        public virtual void GenerateIncludes()
        {
            WriteInclude("Sources.h", CInclude.IncludeKind.Quoted);
            WriteInclude("Types.h", CInclude.IncludeKind.Quoted);
        }

        public virtual void GenerateForwardDecls() { }
    }
    #endregion
}
