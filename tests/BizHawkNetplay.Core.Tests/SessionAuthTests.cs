using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests
{
    /// <summary>
    /// The password proof: a peer proves it knows the password without sending it, in a way that can't
    /// be echoed back, reflected, or replayed to another session.
    /// </summary>
    public class SessionAuthTests
    {
        [Fact]
        public void SamePasswordAndNonces_ProduceMatchingRoleProof()
        {
            var host = SessionAuth.NewNonce();
            var join = SessionAuth.NewNonce();
            // The joiner computes what it expects the host to send; the host computes its own proof — equal.
            var hostSends = SessionAuth.Proof("hunter2", SessionAuth.RoleHost, host, join);
            var joinerExpects = SessionAuth.Proof("hunter2", SessionAuth.RoleHost, host, join);
            Assert.True(SessionAuth.FixedTimeEquals(hostSends, joinerExpects));
        }

        [Fact]
        public void RoleTag_MakesHostAndJoinProofsDiffer()
        {
            // Reflection guard: an attacker can't bounce the host's own proof back as the joiner's.
            var host = SessionAuth.NewNonce();
            var join = SessionAuth.NewNonce();
            Assert.False(SessionAuth.FixedTimeEquals(
                SessionAuth.Proof("pw", SessionAuth.RoleHost, host, join),
                SessionAuth.Proof("pw", SessionAuth.RoleJoin, host, join)));
        }

        [Fact]
        public void WrongPassword_ProducesADifferentProof()
        {
            var host = SessionAuth.NewNonce();
            var join = SessionAuth.NewNonce();
            Assert.False(SessionAuth.FixedTimeEquals(
                SessionAuth.Proof("right", SessionAuth.RoleJoin, host, join),
                SessionAuth.Proof("wrong", SessionAuth.RoleJoin, host, join)));
        }

        [Fact]
        public void FreshNonce_MakesEachProofSingleUse()
        {
            var host1 = SessionAuth.NewNonce();
            var host2 = SessionAuth.NewNonce();
            var join = SessionAuth.NewNonce();
            // Same password + role, different session nonce -> a captured proof can't be replayed.
            Assert.False(SessionAuth.FixedTimeEquals(
                SessionAuth.Proof("pw", SessionAuth.RoleJoin, host1, join),
                SessionAuth.Proof("pw", SessionAuth.RoleJoin, host2, join)));
        }

        [Fact]
        public void EmptyPassword_IsAnOpenSession_ButStillRoleBound()
        {
            var host = SessionAuth.NewNonce();
            var join = SessionAuth.NewNonce();
            // Both ends derive the same role proof from "" -> anyone may join (no secret needed)...
            Assert.True(SessionAuth.FixedTimeEquals(
                SessionAuth.Proof("", SessionAuth.RoleJoin, host, join),
                SessionAuth.Proof("", SessionAuth.RoleJoin, host, join)));
            // ...but the role tag still separates the two directions.
            Assert.False(SessionAuth.FixedTimeEquals(
                SessionAuth.Proof("", SessionAuth.RoleHost, host, join),
                SessionAuth.Proof("", SessionAuth.RoleJoin, host, join)));
        }

        [Fact]
        public void HexNonce_RoundTrips_AndRejectsGarbage()
        {
            var n = SessionAuth.NewNonce();
            Assert.Equal(n, SessionAuth.FromHex(SessionAuth.ToHex(n)));
            Assert.Null(SessionAuth.FromHex("nothex"));
            Assert.Null(SessionAuth.FromHex("abc")); // odd length
        }

        [Fact]
        public void NewSessionId_IsNonZero()
        {
            Assert.NotEqual(0UL, SessionAuth.NewSessionId());
        }
    }
}
