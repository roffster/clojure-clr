﻿/**
 *   Copyright (c) Rich Hickey. All rights reserved.
 *   The use and distribution terms for this software are covered by the
 *   Eclipse Public License 1.0 (http://opensource.org/licenses/eclipse-1.0.php)
 *   which can be found in the file epl-v10.html at the root of this distribution.
 *   By using this software in any fashion, you are agreeing to be bound by
 * 	 the terms of this license.
 *   You must not remove this notice, or any other, from this software.
 **/

/**
 *   Author: David Miller
 **/

using System;
using System.Collections.Generic;

#if CLR2
using Microsoft.Scripting.Ast;
#else
using System.Linq.Expressions;
#endif
using System.Reflection;
using System.Reflection.Emit;
using Microsoft.Scripting.Generation;

namespace clojure.lang.CljCompiler.Ast
{
    sealed class NewInstanceExpr : ObjExpr
    {
        #region Data

        Dictionary<IPersistentVector,List<MethodInfo>> _methodMap;

        #endregion

        #region C-tors

        public NewInstanceExpr(object tag)
            : base(tag)
        {
        }

        #endregion

        #region Parsing
        
        public sealed class DefTypeParser : IParser
        {
            public Expr Parse(ParserContext pcon, object frm)
            {
                // frm is: (deftype* tagname classname [fields] :implements [interfaces] :tag tagname methods*)

                ISeq rform = (ISeq)frm;
                rform = RT.next(rform);

                string tagname = ((Symbol)rform.first()).ToString();
                rform = rform.next();
                Symbol classname = (Symbol)rform.first();
                rform = rform.next();
                IPersistentVector fields = (IPersistentVector)rform.first();
                rform = rform.next();
                IPersistentMap opts = PersistentHashMap.EMPTY;
                while (rform != null && rform.first() is Keyword)
                {
                    opts = opts.assoc(rform.first(), RT.second(rform));
                    rform = rform.next().next();
                }

                ObjExpr ret = Build((IPersistentVector)RT.get(opts, Compiler.ImplementsKeyword, PersistentVector.EMPTY), fields, null, tagname, classname,
                             (Symbol)RT.get(opts, RT.TagKey), rform, frm);

                return ret;
            }
        }


        public sealed class ReifyParser : IParser
        {
            public Expr Parse(ParserContext pcon, object frm)
            {
                // frm is:  (reify this-name? [interfaces] (method-name [args] body)* )
                ISeq form = (ISeq)frm;
                ObjMethod enclosingMethod = (ObjMethod)Compiler.MethodVar.deref();
                string baseName = enclosingMethod != null
                    ? (ObjExpr.TrimGenId(enclosingMethod.Objx.Name) + "$")
                    : (Compiler.munge(Compiler.CurrentNamespace.Name.Name) + "$");
                string simpleName = "reify__" + RT.nextID();
                string className = baseName + simpleName;

                ISeq rform = RT.next(form);

                IPersistentVector interfaces = ((IPersistentVector)RT.first(rform)).cons(Symbol.intern("clojure.lang.IObj"));

                rform = RT.next(rform);

                ObjExpr ret = Build(interfaces, null, null, className, Symbol.intern(className), null, rform,frm);
                IObj iobj = frm as IObj;

                if (iobj != null && iobj.meta() != null)
                    return new MetaExpr(ret, MapExpr.Parse(pcon.EvalOrExpr(),iobj.meta()));
                else
                    return ret;
            }
        }

        internal static ObjExpr Build(
            IPersistentVector interfaceSyms, 
            IPersistentVector fieldSyms, 
            Symbol thisSym,
            string tagName, 
            Symbol className, 
            Symbol typeTag, 
            ISeq methodForms,
            Object frm)
        {
            NewInstanceExpr ret = new NewInstanceExpr(null);
            ret._src = frm;
            ret._name = className.ToString();
            ret._classMeta = GenInterface.ExtractAttributes(RT.meta(className));
            ret.InternalName = ret.Name;  // ret.Name.Replace('.', '/');
            // Java: ret.objtype = Type.getObjectType(ret.internalName);

            if (thisSym != null)
                ret._thisName = thisSym.Name;

            if (fieldSyms != null)
            {
                IPersistentMap fmap = PersistentHashMap.EMPTY;
                object[] closesvec = new object[2 * fieldSyms.count()];
                for (int i = 0; i < fieldSyms.count(); i++)
                {
                    Symbol sym = (Symbol)fieldSyms.nth(i);
                    LocalBinding lb = new LocalBinding(-1, sym, null, new MethodParamExpr(Compiler.TagType(Compiler.TagOf(sym))), false, false);
                    fmap = fmap.assoc(sym, lb);
                    closesvec[i * 2] = lb;
                    closesvec[i * 2 + 1] = lb;
                }
                // Java TODO: inject __meta et al into closes - when?
                // use array map to preserve ctor order
                ret._closes = new PersistentArrayMap(closesvec);
                ret._fields = fmap;
                for (int i = fieldSyms.count() - 1; i >= 0 && ((Symbol)fieldSyms.nth(i)).Name.StartsWith("__"); --i)
                    ret._altCtorDrops++;
            }

            // Java TODO: set up volatiles
            //ret._volatiles = PersistentHashSet.create(RT.seq(RT.get(ret._optionsMap, volatileKey)));

            IPersistentVector interfaces = PersistentVector.EMPTY;
            for (ISeq s = RT.seq(interfaceSyms); s != null; s = s.next())
            {
                Type t = (Type)Compiler.Resolve((Symbol)s.first());
                if (!t.IsInterface)
                    throw new ParseException("only interfaces are supported, had: " + t.Name);
                interfaces = interfaces.cons(t);
            }
            Type superClass = typeof(Object);

            Dictionary<IPersistentVector, List<MethodInfo>> overrideables;
            GatherMethods(superClass, RT.seq(interfaces), out overrideables);

            ret._methodMap = overrideables;

            //string[] inames = InterfaceNames(interfaces);

            Type stub = CompileStub(superClass, ret, SeqToTypeArray(interfaces),frm);
            Symbol thisTag = Symbol.intern(null, stub.FullName);
            //Symbol stubTag = Symbol.intern(null,stub.FullName);
            //Symbol thisTag = Symbol.intern(null, tagName);

            // Needs its own GenContext so it has its own DynInitHelper
            // Can't reuse Compiler.EvalContext if it is a DefType because we have to use the given name and will get a conflict on redefinition
            GenContext context = Compiler.CompilerContextVar.get() as GenContext ?? (ret.IsDefType ? GenContext.CreateWithExternalAssembly("deftype" + RT.nextID().ToString(),".dll",true) : Compiler.EvalContext);
            GenContext genC = context.WithNewDynInitHelper(ret.InternalName + "__dynInitHelper_" + RT.nextID().ToString());
            //genC.FnCompileMode = FnMode.Full;

            try
            {
                Var.pushThreadBindings(
                    RT.map(
                        Compiler.ConstantsVar, PersistentVector.EMPTY,
                        Compiler.ConstantIdsVar, new IdentityHashMap(),
                        Compiler.KeywordsVar, PersistentHashMap.EMPTY,
                        Compiler.VarsVar, PersistentHashMap.EMPTY,
                        Compiler.KeywordCallsitesVar, PersistentVector.EMPTY,
                        Compiler.ProtocolCallsitesVar, PersistentVector.EMPTY,
                        Compiler.VarCallsitesVar, Compiler.EmptyVarCallSites(),
                        Compiler.NoRecurVar, null,
                        Compiler.CompilerContextVar, genC
                        ));

                if (ret.IsDefType)
                {
                    Var.pushThreadBindings(
                        RT.map(
                            Compiler.MethodVar, null,
                            Compiler.LocalEnvVar, ret._fields,
                            Compiler.CompileStubSymVar, Symbol.intern(null, tagName),
                            Compiler.CompileStubClassVar, stub
                            ));
                    ret._hintedFields = RT.subvec(fieldSyms, 0, fieldSyms.count() - ret._altCtorDrops);
                }
                // now (methodname [args] body)*
                // TODO: SourceLocation?
                //ret.line = (Integer)LINE.deref();
                IPersistentCollection methods = null;
                for (ISeq s = methodForms; s != null; s = RT.next(s))
                {
                    NewInstanceMethod m = NewInstanceMethod.Parse(ret, (ISeq)RT.first(s), thisTag, overrideables);
                    methods = RT.conj(methods, m);
                }

                ret.Methods = methods;
                ret.Keywords = (IPersistentMap)Compiler.KeywordsVar.deref();
                ret.Vars = (IPersistentMap)Compiler.VarsVar.deref();
                ret.Constants = (PersistentVector)Compiler.ConstantsVar.deref();
                ret._constantsID = RT.nextID();
                ret.KeywordCallsites = (IPersistentVector)Compiler.KeywordCallsitesVar.deref();
                ret.ProtocolCallsites = (IPersistentVector)Compiler.ProtocolCallsitesVar.deref();
                ret.VarCallsites = (IPersistentSet)Compiler.VarCallsitesVar.deref();
            }
            finally
            {
                if (ret.IsDefType)
                    Var.popThreadBindings();
                Var.popThreadBindings();
            }

            // TOD:  Really, the first stub here should be 'superclass' but can't handle hostexprs nested in method bodies -- reify method compilation takes place before this sucker is compiled, so can't replace the call.
            // Might be able to flag stub classes and not try to convert, leading to a dynsite.
            ret.Compile(stub, stub, interfaces, false, genC);
            Compiler.RegisterDuplicateType(ret.CompiledType);

            return ret;
        }

        private static Type[] SeqToTypeArray(IPersistentVector interfaces)
        {
            Type[] types = new Type[interfaces.count()];
            for (int i = 0; i < interfaces.count(); i++)
                types[i] = (Type)interfaces.nth(i);

            return types;
        }

        /***
         * Current host interop uses reflection, which requires pre-existing classes
         * Work around this by:
         * Generate a stub class that has the same interfaces and fields as the class we are generating.
         * Use it as a type hint for this, and bind the simple name of the class to this stub (in resolve etc)
         * Unmunge the name (using a magic prefix) on any code gen for classes
         */
        static Type CompileStub(Type super, NewInstanceExpr ret, Type[] interfaces, Object frm)
        {

            //GenContext context = Compiler.COMPILER_CONTEXT.get() as GenContext ?? Compiler.EvalContext;
            //GenContext context = Compiler.COMPILER_CONTEXT.get() as GenContext ?? new GenContext("stub" + RT.nextID().ToString(), ".dll", ".", CompilerMode.Immediate);
            GenContext context = Compiler.CompilerContextVar.get() as GenContext ?? GenContext.CreateWithExternalAssembly("stub" + RT.nextID().ToString(), ".dll", false);
            //GenContext context = GenContext.CreateWithInternalAssembly("stub" + RT.nextID().ToString(), false);
            TypeBuilder tb = context.ModuleBuilder.DefineType(Compiler.CompileStubPrefix + "." + ret.InternalName, TypeAttributes.Public | TypeAttributes.Abstract, super, interfaces);

            tb.DefineDefaultConstructor(MethodAttributes.Public);

            // instance fields for closed-overs
            for (ISeq s = RT.keys(ret.Closes); s != null; s = s.next())
            {
                LocalBinding lb = (LocalBinding)s.first();
                FieldAttributes access = FieldAttributes.Public;

                // TODO: FIgure out Volatile
                if (!ret.IsVolatile(lb))
                    access |= FieldAttributes.InitOnly;

                if (lb.PrimitiveType != null)
                    tb.DefineField(lb.Name, lb.PrimitiveType, access);
                else
                    tb.DefineField(lb.Name, typeof(Object), access);
            }

            // ctor that takes closed-overs and does nothing
            if (ret.CtorTypes().Length > 0)
            {
                ConstructorBuilder cb = tb.DefineConstructor(MethodAttributes.Public, CallingConventions.HasThis, ret.CtorTypes());
                ILGen ilg = new ILGen(cb.GetILGenerator());
                ilg.EmitLoadArg(0);
                ilg.Emit(OpCodes.Call, super.GetConstructor(Type.EmptyTypes));
                ilg.Emit(OpCodes.Ret);


                if (ret._altCtorDrops > 0)
                {
                    Type[] ctorTypes = ret.CtorTypes();
                    int newLen = ctorTypes.Length - ret._altCtorDrops;
                    if (newLen > 0)
                    {
                        Type[] altCtorTypes = new Type[newLen];
                        for (int i = 0; i < altCtorTypes.Length; i++)
                            altCtorTypes[i] = ctorTypes[i];
                        ConstructorBuilder cb2 = tb.DefineConstructor(MethodAttributes.Public, CallingConventions.HasThis, altCtorTypes);
                        ILGen ilg2 = new ILGen(cb2.GetILGenerator());
                        ilg2.EmitLoadArg(0);
                        for (int i = 0; i < newLen; i++)
                            ilg2.EmitLoadArg(i + 1);
                        for (int i = 0; i < ret._altCtorDrops; i++)
                            ilg2.EmitNull();
                        ilg2.Emit(OpCodes.Call, cb);
                        ilg2.Emit(OpCodes.Ret);
                    }
                }
            }

            Type t = tb.CreateType();
            Compiler.RegisterDuplicateType(t);
            return t;
        }


 
        static string[] InterfaceNames(IPersistentVector interfaces)
        {
            int icnt = interfaces.count();
            string[] inames = icnt > 0 ? new string[icnt] : null;
            for (int i = 0; i < icnt; i++)
                inames[i] = SlashName((Type)interfaces.nth(i));
            return inames;
        }


        static string SlashName(Type t)
        {
            return t.FullName.Replace(',', '/');
        }


        #endregion

        #region Method reflection

        static void GatherMethods(
            Type st,
            ISeq interfaces,
            out Dictionary<IPersistentVector, List<MethodInfo>> overrides)
        {
            Dictionary<IPersistentVector, List<MethodInfo>> allm = new Dictionary<IPersistentVector, List<MethodInfo>>();
            GatherMethods(st, allm);
            for (; interfaces != null; interfaces = interfaces.next())
                GatherMethods((Type)interfaces.first(), allm);

            overrides = allm;
        }

        static void GatherMethods(Type t, Dictionary<IPersistentVector, List<MethodInfo>> mm)
        {
            for (Type mt = t; mt != null; mt = mt.BaseType)
                foreach (MethodInfo m in mt.GetMethods(BindingFlags.FlattenHierarchy | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    ConsiderMethod(m, mm);

            if (t.IsInterface)
                foreach (Type it in t.GetInterfaces())
                    GatherMethods(it, mm);
        }

        static void ConsiderMethod(MethodInfo m, Dictionary<IPersistentVector, List<MethodInfo>> mm)
        {
            IPersistentVector mk = MSig(m);
            if (!(mm.ContainsKey(mk)
                || !(m.IsPublic || m.IsFamily)
                || m.IsStatic
                || m.IsFinal))
                AddMethod(mm, mk, m);
        }

        public static IPersistentVector MSig(MethodInfo m)
        {
            return RT.vector(m.Name, RT.seq(Compiler.GetTypes(m.GetParameters())), m.ReturnType);
        }

        static void AddMethod(Dictionary<IPersistentVector, List<MethodInfo>> mm, IPersistentVector sig, MethodInfo m)
        {
            List<MethodInfo> value;
            if (!mm.TryGetValue(sig, out value))
            {
                value = new List<MethodInfo>();
                mm[sig] = value;
            }
            value.Add(m);
        }

        #endregion

        #region ObjExpr methods

        protected override bool SupportsMeta
        {
            get { return ! IsDefType; }
        }

        #endregion

        #region Code generation

        protected override void GenerateMethods(GenContext context)
        {
            HashSet<MethodInfo> implemented = new HashSet<MethodInfo>();

            for (ISeq s = RT.seq(Methods); s != null; s = s.next())
            {
                NewInstanceMethod method = (NewInstanceMethod)s.first();
                method.GenerateCode(this,context);
                implemented.UnionWith(method.MethodInfos);
            }

            foreach (List<MethodInfo> ms in _methodMap.Values)
                foreach (MethodInfo mi in ms)
                    if (NeedsDummy(mi,implemented))
                        GenerateDummyMethod(context,mi);
        }

        private bool NeedsDummy(MethodInfo mi, HashSet<MethodInfo> implemented)
        {
            return !implemented.Contains(mi) && mi.DeclaringType.IsInterface && !(!IsDefType && mi.DeclaringType == typeof(IObj) || mi.DeclaringType == typeof(IMeta));
        }

        private void GenerateDummyMethod(GenContext context, MethodInfo mi)
        {
            TypeBuilder tb = TypeBuilder;

            MethodBuilder mb = tb.DefineMethod(ExplicitMethodName(mi), MethodAttributes.ReuseSlot | MethodAttributes.Public | MethodAttributes.Virtual, mi.ReturnType, Compiler.GetTypes(mi.GetParameters()));
            ILGen gen = new ILGen(mb.GetILGenerator());
            gen.EmitNew(typeof(NotImplementedException),Type.EmptyTypes);
            gen.Emit(OpCodes.Throw);
            tb.DefineMethodOverride(mb, mi);            
        }

        private static string ExplicitMethodName(MethodInfo mi)
        {
            return mi.DeclaringType.Name + "." + mi.Name;
        }


        protected override void GenerateStatics(GenContext context)
        {
            if (IsDefType)
            {
                TypeBuilder tb = TypeBuilder;

                // getBasis()
                MethodBuilder mbg = tb.DefineMethod("getBasis", MethodAttributes.Public | MethodAttributes.Static, typeof(IPersistentVector), Type.EmptyTypes);
                LambdaExpression lambda = Expression.Lambda(GenerateValue(_hintedFields));
                lambda.CompileToMethod(mbg, context.IsDebuggable);

                if (_fields.count() > _hintedFields.count())
                {
                    // create(IPersistentMap)
                    MethodBuilder mbc = tb.DefineMethod("create", MethodAttributes.Public | MethodAttributes.Static, tb, new Type[] { typeof(IPersistentMap) });
                    ILGen gen = new ILGen(mbc.GetILGenerator());

                    LocalBuilder kwLocal = gen.DeclareLocal(typeof(Keyword));
                    List<LocalBuilder> locals = new List<LocalBuilder>();
                    for (ISeq s = RT.seq(_hintedFields); s != null; s = s.next())
                    {
                        string bName = ((Symbol)s.first()).Name;
                        Type t = Compiler.TagType(Compiler.TagOf(s.first()));

                        // local_kw = Keyword.intern(bname)
                        // local_i = arg_0.valAt(kw,null)
                        gen.EmitLoadArg(0);
                        gen.EmitString(bName);
                        gen.EmitCall(Compiler.Method_Keyword_intern_string);
                        gen.Emit(OpCodes.Dup);
                        gen.Emit(OpCodes.Stloc,kwLocal.LocalIndex);
                        gen.EmitNull();
                        gen.EmitCall(Compiler.Method_IPersistentMap_valAt2);
                        LocalBuilder lb = gen.DeclareLocal(t);
                        locals.Add(lb);
                        if ( t.IsPrimitive )
                            gen.EmitUnbox(t);
                        gen.Emit(OpCodes.Stloc, lb.LocalIndex);

                        // arg_0 = arg_0.without(local_kw);
                        gen.EmitLoadArg(0);
                        gen.Emit(OpCodes.Ldloc,kwLocal.LocalIndex);
                        gen.EmitCall(Compiler.Method_IPersistentMap_without);
                        gen.EmitStoreArg(0);
                    }

                    foreach (LocalBuilder lb in locals)
                        gen.Emit(OpCodes.Ldloc, lb.LocalIndex);
                    gen.EmitNull();
                    gen.EmitLoadArg(0);
                    gen.EmitCall(Compiler.Method_RT_seqOrElse);
                    gen.EmitNew(CtorInfo);

                    gen.Emit(OpCodes.Ret);
                }
            }
        }

        #endregion
    }
}
