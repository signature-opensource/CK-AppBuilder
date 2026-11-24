using CK.Core;
using CK.TypeCache;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace CK.Setup;

/// <summary>
/// Finds the <see cref="IAppBuilderContributor"/> declared by the assemblies of the builder's folder
/// (<see cref="AppBuilderContributorAttribute"/>), orders them like any resource group (their <c>[Requires]</c> and
/// <c>[RequiredBy]</c> read by <see cref="ResPackageRelationReader"/>, their constructor parameters as implicit
/// <c>[Requires]</c>), creates them in this order and runs them.
/// </summary>
static class AppBuilderContributors
{
    /// <summary>
    /// Finds and creates the contributors of a folder, in the order of their requirements.
    /// </summary>
    public static IReadOnlyList<IAppBuilderContributor>? Create( IActivityMonitor monitor, string folder )
    {
        using var _ = monitor.OpenInfo( "Creating the App builder contributors." );
        var types = Discover( monitor, folder );
        if( types == null ) return null;
        var order = Order( monitor, types );
        if( order == null ) return null;
        var created = new Dictionary<Type, IAppBuilderContributor>();
        var ordered = new List<IAppBuilderContributor>( order.Count );
        foreach( var t in order )
        {
            var ctor = t.GetConstructors()[0];
            var args = ctor.GetParameters().Select( p => (object)created[types.First( p.ParameterType.IsAssignableFrom )] ).ToArray();
            IAppBuilderContributor c;
            try
            {
                c = (IAppBuilderContributor)ctor.Invoke( args );
            }
            catch( TargetInvocationException ex ) when( ex.InnerException != null )
            {
                monitor.Error( $"Unable to create the App builder contributor '{t.ToCSharpName()}'.", ex.InnerException );
                return null;
            }
            created.Add( t, c );
            ordered.Add( c );
        }
        monitor.Info( ordered.Count == 0
                        ? "No contributor."
                        : $"{ordered.Count} contributor(s): '{ordered.Select( c => c.GetType().ToCSharpName() ).Concatenate( "', '" )}'." );
        return ordered;
    }

    /// <summary>
    /// Runs the contributors (created by <see cref="Create(IActivityMonitor, string)"/>).
    /// </summary>
    public static bool Run( IActivityMonitor monitor, AppBuilderContext context, IReadOnlyList<IAppBuilderContributor> ordered )
    {
        using var _ = monitor.OpenInfo( "Running the App builder contributors." );
        bool success = true;
        foreach( var c in ordered )
        {
            using( monitor.OpenTrace( $"Contribute: '{c.GetType().ToCSharpName()}'." ) )
            {
                success &= Call( monitor, c, () => c.Contribute( monitor, context ) );
            }
        }
        if( !success ) return false;
        foreach( var c in ordered )
        {
            success &= Call( monitor, c, () => c.OnContributed( monitor, context ) );
        }
        return success;

        static bool Call( IActivityMonitor monitor, IAppBuilderContributor c, Func<bool> f )
        {
            try
            {
                return f();
            }
            catch( Exception ex )
            {
                monitor.Error( $"Contributor '{c.GetType().ToCSharpName()}' failed.", ex );
                return false;
            }
        }
    }

    static IReadOnlyList<Type>? Discover( IActivityMonitor monitor, string folder )
    {
        var types = new List<Type>();
        bool success = true;
        foreach( var file in Directory.EnumerateFiles( folder, "*.dll" ).Order( StringComparer.OrdinalIgnoreCase ) )
        {
            var fileName = Path.GetFileName( file );
            // The framework and the third party libraries don't declare contributors.
            if( fileName.StartsWith( "System.", StringComparison.Ordinal )
                || fileName.StartsWith( "Microsoft.", StringComparison.Ordinal ) )
            {
                continue;
            }
            Assembly a;
            try
            {
                a = AssemblyLoadContext.Default.LoadFromAssemblyName( AssemblyName.GetAssemblyName( file ) );
            }
            catch( BadImageFormatException )
            {
                // A native library.
                continue;
            }
            catch( Exception ex )
            {
                monitor.Warn( $"Unable to load '{file}': its contributors (if any) are ignored.", ex );
                continue;
            }
            foreach( var attr in a.GetCustomAttributes<AppBuilderContributorAttribute>() )
            {
                var t = attr.ContributorType;
                if( !typeof( IAppBuilderContributor ).IsAssignableFrom( t ) || t.IsAbstract )
                {
                    monitor.Error( $"[AppBuilderContributor( typeof( {t.ToCSharpName()} ) )] of '{a.GetName().Name}': the type must be a concrete {nameof( IAppBuilderContributor )}." );
                    success = false;
                }
                else if( !types.Contains( t ) ) types.Add( t );
            }
        }
        return success ? types : null;
    }

    // Orders the contributors: the requirements (the constructor parameters and the [Requires]) come first, the
    // [RequiredBy] targets after. The order of the discovery breaks the ties. Returns null on error.
    static List<Type>? Order( IActivityMonitor monitor, IReadOnlyList<Type> types )
    {
        var before = types.ToDictionary( t => t, _ => new List<Type>() );
        var typeCache = new GlobalTypeCache();
        bool success = true;
        foreach( var t in types )
        {
            var ctors = t.GetConstructors();
            if( ctors.Length != 1 )
            {
                monitor.Error( $"The App builder contributor '{t.ToCSharpName()}' must have exactly one public constructor (its parameters are the contributors it requires)." );
                success = false;
                continue;
            }
            foreach( var p in ctors[0].GetParameters() )
            {
                var required = types.FirstOrDefault( p.ParameterType.IsAssignableFrom );
                if( required == null )
                {
                    monitor.Error( $"The App builder contributor '{t.ToCSharpName()}' requires '{p.ParameterType.ToCSharpName()}' that is not a contributor of the builder (a missing package reference?)." );
                    success = false;
                }
                else before[t].Add( required );
            }
            var visitor = new RelationVisitor( monitor, t, types, before );
            success &= ResPackageRelationReader.Read( monitor, typeCache.Get( t ), ref visitor ) && visitor.Success;
        }
        if( !success ) return null;

        var ordered = new List<Type>( types.Count );
        var done = new HashSet<Type>();
        var visiting = new List<Type>();
        foreach( var t in types )
        {
            if( !Visit( monitor, t, before, done, visiting, ordered ) ) return null;
        }
        return ordered;

        static bool Visit( IActivityMonitor monitor, Type t, Dictionary<Type, List<Type>> before, HashSet<Type> done, List<Type> visiting, List<Type> ordered )
        {
            if( done.Contains( t ) ) return true;
            if( visiting.Contains( t ) )
            {
                monitor.Error( $"Cycle between the App builder contributors: '{visiting.Skip( visiting.IndexOf( t ) ).Append( t ).Select( v => v.ToCSharpName() ).Concatenate( "' -> '" )}'." );
                return false;
            }
            visiting.Add( t );
            foreach( var r in before[t] )
            {
                if( !Visit( monitor, r, before, done, visiting, ordered ) ) return false;
            }
            visiting.RemoveAt( visiting.Count - 1 );
            done.Add( t );
            ordered.Add( t );
            return true;
        }
    }

    // Receives the relations of a contributor: only [Requires] and [RequiredBy] (and their optional variants) between
    // the contributors of the builder are meaningful.
    struct RelationVisitor : IResPackageRelationVisitor
    {
        readonly IActivityMonitor _monitor;
        readonly Type _type;
        readonly IReadOnlyList<Type> _types;
        readonly Dictionary<Type, List<Type>> _before;

        public RelationVisitor( IActivityMonitor monitor, Type type, IReadOnlyList<Type> types, Dictionary<Type, List<Type>> before )
        {
            _monitor = monitor;
            _type = type;
            _types = types;
            _before = before;
            Success = true;
        }

        public bool Success { get; private set; }

        public void OnPackage( ResPackageDescriptor.Ref package )
        {
            _monitor.Error( $"The App builder contributor '{_type.ToCSharpName()}' declares a package ('{package}'): a contributor is not in a package." );
            Success = false;
        }

        public void OnRelation( ResRelationKind kind, ResPackageDescriptor.Ref target )
        {
            if( kind is ResRelationKind.Children or ResRelationKind.Groups )
            {
                _monitor.Error( $"The App builder contributor '{_type.ToCSharpName()}' declares [{kind}] ('{target}'): only [Requires] and [RequiredBy] order the contributors." );
                Success = false;
                return;
            }
            var targetType = target.AsType;
            var name = target.FullName;
            var found = targetType != null
                            ? _types.FirstOrDefault( targetType.IsAssignableFrom )
                            : _types.FirstOrDefault( t => t.FullName == name );
            if( found == null )
            {
                if( !target.IsOptional )
                {
                    _monitor.Error( $"The App builder contributor '{_type.ToCSharpName()}' declares [{kind}] '{target}' that is not a contributor of the builder (a missing package reference?)." );
                    Success = false;
                }
                return;
            }
            if( kind == ResRelationKind.Requires ) _before[_type].Add( found );
            else _before[found].Add( _type );
        }
    }
}
