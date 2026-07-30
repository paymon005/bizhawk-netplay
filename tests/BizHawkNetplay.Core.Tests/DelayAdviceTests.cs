using BizHawkNetplay.Core.Session;
using Xunit;

namespace BizHawkNetplay.Core.Tests
{
    /// <summary>
    /// The advice a player reads when their link is bad. It has been wrong twice — once naming a
    /// control that could not change the outcome, once telling everyone to reconnect for a change the
    /// host can now apply live — and both shipped because a string built inside a UI callback is not
    /// reachable by anything that runs.
    /// </summary>
    public class DelayAdviceTests
    {
        [Fact]
        public void NobodyIsToldToReconnect()
        {
            // The regression that shipped in v0.20.0 and appeared five times in one test session.
            foreach (bool isHost in new[] { true, false })
                foreach (bool auto in new[] { true, false })
                    foreach (int max in new[] { 2, 8, 20 })
                    {
                        string s = DelayAdvice.Remedy(isHost, suggested: 9, autoFromPing: auto, autoMax: max);
                        Assert.DoesNotContain("reconnect", s, System.StringComparison.OrdinalIgnoreCase);
                        Assert.DoesNotContain("stays fixed", s);
                        Assert.Contains("Apply changes", s);
                        Assert.Contains("9", s);
                    }
        }

        [Fact]
        public void AJoinerIsToldWhatToAskFor_NotWhichBoxToTick()
        {
            string joiner = DelayAdvice.Remedy(isHost: false, suggested: 4, autoFromPing: false, autoMax: 8);

            Assert.Contains("Ask the host", joiner);
            // A joiner cannot see or change these, so naming them is noise at best and a wild goose
            // chase at worst.
            Assert.DoesNotContain("Auto from ping", joiner);
            Assert.DoesNotContain("Auto max", joiner);
        }

        [Fact]
        public void TheHostIsToldWhyTheLobbyDidNotAlreadyPickThisNumber()
        {
            // Auto off: the cap is inert, so pointing at it is the wrong advice. Name the box.
            string autoOff = DelayAdvice.Remedy(true, suggested: 6, autoFromPing: false, autoMax: 8);
            Assert.Contains("\"Auto from ping\" is off", autoOff);
            Assert.DoesNotContain("raise Auto max", autoOff);

            // Auto on but capped below the answer: the cap really is why, so say so.
            string capped = DelayAdvice.Remedy(true, suggested: 12, autoFromPing: true, autoMax: 8);
            Assert.Contains("capped at 8", capped);
            Assert.Contains("raise Auto max", capped);

            // Auto on with room: neither control is at fault; the link simply got worse.
            string roomy = DelayAdvice.Remedy(true, suggested: 6, autoFromPing: true, autoMax: 20);
            Assert.Contains("measured a faster link at connect", roomy);
            Assert.DoesNotContain("capped", roomy);
        }

        [Fact]
        public void TheActionIsTheSameWhicheverWayTheDelayIsWrong()
        {
            // ApplyNow is what the over-delayed warning uses. It must carry the action and none of the
            // "why the lobby chose too low a number" tail, which is the opposite problem.
            string over = DelayAdvice.ApplyNow(isHost: true, suggested: 3);
            Assert.Contains("Set Input delay to 3", over);
            Assert.Contains("Apply changes", over);
            Assert.DoesNotContain("Auto", over);

            Assert.StartsWith(over, DelayAdvice.Remedy(true, 3, autoFromPing: true, autoMax: 20));
        }
    }
}
