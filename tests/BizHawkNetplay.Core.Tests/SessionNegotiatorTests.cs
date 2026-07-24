using System.Collections.Generic;
using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests
{
    public class SessionNegotiatorTests
    {
        private static PeerIdentity Id(
            int protocol = 1, string rom = "ROMHASH", string core = "GPGX",
            string coreVer = "2.11.1.0", string sync = "SYNC1",
            bool deterministic = true, int depth = 20, string layout = "L0")
            => new PeerIdentity(protocol, rom, core, coreVer, sync,
                new[] { layout, "L1" }, deterministic, depth);

        private static SessionPreferences Pref(int delay = 2, bool rollback = false, string password = "")
            => new SessionPreferences(delay, rollback, SessionPreferences.HashPassword(password));

        [Fact]
        public void MatchingPeers_AcceptLockstep()
        {
            var r = SessionNegotiator.Negotiate(Id(), Id(), Pref(), Pref());
            Assert.True(r.Accepted);
            Assert.Equal(SyncMode.Lockstep, r.Mode);
        }

        [Fact]
        public void InputDelay_TakesTheLargerAsk()
        {
            var r = SessionNegotiator.Negotiate(Id(), Id(), Pref(delay: 2), Pref(delay: 5));
            Assert.True(r.Accepted);
            Assert.Equal(5, r.InputDelay);
        }

        [Theory]
        [InlineData("ROM")]
        [InlineData("CORE")]
        [InlineData("COREVER")]
        [InlineData("SYNC")]
        [InlineData("PROTO")]
        [InlineData("LAYOUT")]
        public void AnyIdentityMismatch_IsRejected(string which)
        {
            var local = Id();
            PeerIdentity remote = which switch
            {
                "ROM" => Id(rom: "OTHER"),
                "CORE" => Id(core: "NesHawk"),
                "COREVER" => Id(coreVer: "2.10.0.0"),
                "SYNC" => Id(sync: "SYNC2"),
                "PROTO" => Id(protocol: 2),
                "LAYOUT" => Id(layout: "DIFF"),
                _ => Id(),
            };
            var r = SessionNegotiator.Negotiate(local, remote, Pref(), Pref());
            Assert.False(r.Accepted);
            Assert.False(string.IsNullOrEmpty(r.RejectReason));
        }

        [Fact]
        public void SessionPassword_MustMatch()
        {
            // Same password on both sides -> accepted.
            Assert.True(SessionNegotiator.Negotiate(Id(), Id(), Pref(password: "hunter2"), Pref(password: "hunter2")).Accepted);
            // No password on either side -> accepted.
            Assert.True(SessionNegotiator.Negotiate(Id(), Id(), Pref(), Pref()).Accepted);
            // Different passwords -> rejected.
            Assert.False(SessionNegotiator.Negotiate(Id(), Id(), Pref(password: "hunter2"), Pref(password: "letmein")).Accepted);
            // One side set a password, the other didn't -> rejected.
            Assert.False(SessionNegotiator.Negotiate(Id(), Id(), Pref(password: "hunter2"), Pref()).Accepted);
            // The password isn't sent in the clear — only its hash crosses the wire.
            Assert.DoesNotContain("hunter2", SessionPreferences.HashPassword("hunter2"));
        }

        [Fact]
        public void NonDeterministicEitherSide_IsRejected()
        {
            Assert.False(SessionNegotiator.Negotiate(Id(deterministic: false), Id(), Pref(), Pref()).Accepted);
            Assert.False(SessionNegotiator.Negotiate(Id(), Id(deterministic: false), Pref(), Pref()).Accepted);
        }

        [Fact]
        public void Rollback_OnlyWhenBothOptInAndBothQualify()
        {
            // Both want it, both deep enough -> rollback.
            var r1 = SessionNegotiator.Negotiate(Id(depth: 20), Id(depth: 20),
                Pref(rollback: true), Pref(rollback: true));
            Assert.Equal(SyncMode.Rollback, r1.Mode);

            // Both want it, but the worst peer is too shallow -> lockstep.
            var r2 = SessionNegotiator.Negotiate(Id(depth: 20), Id(depth: 3),
                Pref(rollback: true), Pref(rollback: true));
            Assert.Equal(SyncMode.Lockstep, r2.Mode);

            // One peer didn't opt in -> lockstep even though both qualify.
            var r3 = SessionNegotiator.Negotiate(Id(depth: 20), Id(depth: 20),
                Pref(rollback: true), Pref(rollback: false));
            Assert.Equal(SyncMode.Lockstep, r3.Mode);
        }
    }
}
