// N.I.N.A. Polaris
// Copyright (C) 2024-2026 Daniel Wagner (DanWBR) and the N.I.N.A. Polaris contributors
//
// This program is free software: you can redistribute it and/or modify it
// under the terms of the GNU Affero General Public License as published by
// the Free Software Foundation, either version 3 of the License, or (at your
// option) any later version.
//
// This program is distributed in the hope that it will be useful, but WITHOUT
// ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
// FITNESS FOR A PARTICULAR PURPOSE. See the GNU Affero General Public License
// for more details. You should have received a copy of the license along with
// this program. If not, see <https://www.gnu.org/licenses/>.

using System.Net.Sockets;
using System.Reflection;
using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Auto-connect runs on every boot and tries PHD2 and INDI whether or not they
/// are running. Most of the time they are not, and that is normal: the operator
/// starts PHD2 later, or never. Logging a full socket stack trace for it made a
/// healthy startup read as a fault (field report), and a log full of harmless
/// traces is a log people stop reading.
///
/// <para>The line these tests defend: "nothing is listening there" is a state
/// of the world and gets one line; anything else keeps its trace.</para>
/// </summary>
[TestFixture]
public class AutoConnectNoiseTests {

    private static bool Expected(Exception ex) {
        var m = typeof(HardwareAutoConnectService).GetMethod("IsExpectedUnreachable",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)m.Invoke(null, new object?[] { ex })!;
    }

    private static string Describe(Exception ex) {
        var m = typeof(HardwareAutoConnectService).GetMethod("DescribeUnreachable",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        return (string)m.Invoke(null, new object?[] { ex })!;
    }

    /// <summary>The exact case from the report: PHD2 is not running, so the
    /// port refuses the connection.</summary>
    [Test]
    public void ConnectionRefusedIsRoutine() {
        Assert.That(Expected(new SocketException((int)SocketError.ConnectionRefused)), Is.True);
    }

    [TestCase(SocketError.TimedOut)]
    [TestCase(SocketError.HostNotFound)]
    [TestCase(SocketError.HostUnreachable)]
    [TestCase(SocketError.NetworkUnreachable)]
    [TestCase(SocketError.TryAgain)]
    public void TheOtherWaysOfSayingNothingIsThereAreRoutineToo(SocketError err) {
        Assert.That(Expected(new SocketException((int)err)), Is.True, err.ToString());
    }

    /// <summary>Our own 5 s budget expiring is not a defect either.</summary>
    [Test]
    public void OurOwnTimeoutIsRoutine() {
        Assert.That(Expected(new OperationCanceledException()), Is.True);
        Assert.That(Expected(new TimeoutException()), Is.True);
    }

    /// <summary>TcpClient wraps the socket error, so the check has to look
    /// inside or every real case would fall through to the noisy branch.
    /// </summary>
    [Test]
    public void AWrappedSocketErrorIsStillRecognised() {
        var wrapped = new IOException("connect failed",
            new SocketException((int)SocketError.ConnectionRefused));
        Assert.That(Expected(wrapped), Is.True);

        var aggregated = new AggregateException(
            new SocketException((int)SocketError.ConnectionRefused));
        Assert.That(Expected(aggregated), Is.True);
    }

    /// <summary>The half that matters just as much: a genuine surprise must
    /// NOT be quietened, or this fix would trade noise for blindness.</summary>
    [Test]
    public void AnUnexpectedFailureKeepsItsTrace() {
        Assert.That(Expected(new InvalidOperationException("protocol desync")), Is.False);
        Assert.That(Expected(new NullReferenceException()), Is.False);
        Assert.That(Expected(new SocketException((int)SocketError.AccessDenied)), Is.False);
    }

    /// <summary>The one line still has to say WHY: refused and not-found are
    /// different problems and the operator acts differently on each.</summary>
    [Test]
    public void TheReasonSurvivesIntoTheMessage() {
        Assert.That(Describe(new SocketException((int)SocketError.ConnectionRefused)),
            Is.EqualTo("ConnectionRefused"));
        Assert.That(Describe(new SocketException((int)SocketError.HostNotFound)),
            Is.EqualTo("HostNotFound"));
        Assert.That(Describe(new IOException("x",
            new SocketException((int)SocketError.TimedOut))), Is.EqualTo("TimedOut"));
        Assert.That(Describe(new OperationCanceledException()), Is.EqualTo("timed out"));
    }
}
