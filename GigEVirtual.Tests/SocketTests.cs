// --------------------------------------------------------------------------------
// SocketTests.cs
//
// the tests that stream over a real socket and assert on timing. running them
// alongside each other makes them fight for the loopback and the clock, so they
// share a collection that xunit runs on its own.
// --------------------------------------------------------------------------------

using Xunit;

namespace GigEVirtual.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public class SocketTests
{
    public const string Name = "sockets";
}
