using CK.Core;
using CK.Setup;
using NUnit.Framework;
using Shouldly;
using System.Collections.Generic;
using static CK.Testing.MonitorTestHelper;

[assembly: AppBuilderContributor( typeof( CK.AppBuilder.Tests.SecondContributor ) )]
[assembly: AppBuilderContributor( typeof( CK.AppBuilder.Tests.FirstContributor ) )]
[assembly: AppBuilderContributor( typeof( CK.AppBuilder.Tests.ThirdContributor ) )]
[assembly: AppBuilderContributor( typeof( CK.AppBuilder.Tests.ZeroContributor ) )]

namespace CK.AppBuilder.Tests;

public sealed class FirstContributor : IAppBuilderContributor
{
    public static readonly List<string> Calls = new();

    public bool Contribute( IActivityMonitor monitor, AppBuilderContext context )
    {
        Calls.Add( "First.Contribute" );
        return true;
    }

    public bool OnContributed( IActivityMonitor monitor, AppBuilderContext context )
    {
        Calls.Add( "First.OnContributed" );
        return true;
    }
}

// Requires the FirstContributor: a constructor parameter is a requirement.
public sealed class SecondContributor : IAppBuilderContributor
{
    public SecondContributor( FirstContributor first ) => First = first;

    public FirstContributor First { get; }

    public bool Contribute( IActivityMonitor monitor, AppBuilderContext context )
    {
        FirstContributor.Calls.Add( "Second.Contribute" );
        return true;
    }
}

// Runs before the FirstContributor: [RequiredBy] (read by the reader of the ResourceSpace).
[RequiredBy<FirstContributor>]
public sealed class ZeroContributor : IAppBuilderContributor
{
    public bool Contribute( IActivityMonitor monitor, AppBuilderContext context )
    {
        FirstContributor.Calls.Add( "Zero.Contribute" );
        return true;
    }
}

// Runs after the SecondContributor: a name based [Requires]. The optional requirement is not a contributor: ignored.
[Requires( "CK.AppBuilder.Tests.SecondContributor" )]
[OptionalRequires( "CK.AppBuilder.Tests.MissingContributor" )]
public sealed class ThirdContributor : IAppBuilderContributor
{
    public bool Contribute( IActivityMonitor monitor, AppBuilderContext context )
    {
        FirstContributor.Calls.Add( "Third.Contribute" );
        return true;
    }
}

[TestFixture]
public class AppBuilderContributorsTests
{
    [Test]
    public void the_contributors_are_found_and_ordered_by_their_requirements()
    {
        FirstContributor.Calls.Clear();
        var context = new AppBuilderContext( new EngineConfiguration(), [] );
        var contributors = AppBuilderContributors.Create( TestHelper.Monitor, TestHelper.BinFolder ).ShouldNotBeNull();
        AppBuilderContributors.Run( TestHelper.Monitor, context, contributors ).ShouldBeTrue();
        // Declared Second first: the requirements order them (Zero by its [RequiredBy], Second by its constructor parameter,
        // Third by its [Requires]).
        FirstContributor.Calls.ShouldBe( ["Zero.Contribute", "First.Contribute", "Second.Contribute", "Third.Contribute", "First.OnContributed"] );
    }
}
