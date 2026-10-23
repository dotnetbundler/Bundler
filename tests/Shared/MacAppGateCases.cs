// MacAppGate 已签分支决策表：AOT 腿（Unit，GateTool 缝注入桩 codesign 输出）与
// sh 腿（Integration，PATH 前置桩 codesign 脚本）共用同一张表——两份实现必须同判。
// 桩只模拟 codesign 的观察面（-dv 的 TeamIdentifier、--verify 出口码、起不来），
// 真 Apple Developer ID 链仍归 special-acceptance SA-P-07。

/// <summary>一侧 bundle 的 codesign 观察面。</summary>
/// <param name="Team">null=未签（-dv 非零）；"not set"=无 Team 签名（ad-hoc/自签）；其余=Team ID。</param>
/// <param name="VerifyRc">codesign --verify --deep --strict 出口码；非零=坏签/篡改。</param>
public sealed record GateSide(string? Team, int VerifyRc = 0)
{
    internal static readonly GateSide Unsigned = new((string?)null);
    internal static readonly GateSide TeamLess = new("not set");
    internal static GateSide Signed(string team, int verifyRc = 0) => new(team, verifyRc);
}

/// <param name="ProbeFails">codesign 起不来（AOT 出口 -1 / sh 出口 127）——必须 fail closed。</param>
/// <param name="Reason">拒绝时双侧消息共有片段；放行时为 null。</param>
public sealed record GateCase(
    string Name,
    GateSide Old,
    GateSide New,
    string? Reason,
    bool ProbeFails = false,
    string OldBundleId = "com.bundler.gate",
    string NewBundleId = "com.bundler.gate")
{
    internal bool Accepts => Reason is null;

    public override string ToString() => Name;

    internal const string ProbeFailedReason = "could not be executed";
    internal const string CrossIdentityReason = "cross-identity";
    internal const string VerificationReason = "codesign verification";
    internal const string DowngradeReason = "downgrade";
    internal const string BundleIdReason = "bundle identifier mismatch";

    internal static readonly GateCase[] All =
    [
        new("BothUnsigned", GateSide.Unsigned, GateSide.Unsigned, null),
        new("SameTeam", GateSide.Signed("TEAMAAAAAA"), GateSide.Signed("TEAMAAAAAA"), null),
        new("CrossTeam", GateSide.Signed("TEAMAAAAAA"), GateSide.Signed("TEAMBBBBBB"), CrossIdentityReason),
        new("SameTeamTampered", GateSide.Signed("TEAMAAAAAA"), GateSide.Signed("TEAMAAAAAA", verifyRc: 1), VerificationReason),
        new("SignedToUnsigned", GateSide.Signed("TEAMAAAAAA"), GateSide.Unsigned, DowngradeReason),
        new("SignedToTeamLess", GateSide.Signed("TEAMAAAAAA"), GateSide.TeamLess, DowngradeReason),
        new("UnsignedToSigned", GateSide.Unsigned, GateSide.Signed("TEAMAAAAAA"), null),
        new("UnsignedToSignedTampered", GateSide.Unsigned, GateSide.Signed("TEAMAAAAAA", verifyRc: 1), VerificationReason),
        // 无 Team 签名视同未签（无身份可比）——已登记语义边界，见 docs/design/update.md。
        new("BothTeamLess", GateSide.TeamLess, GateSide.TeamLess, null),
        new("ProbeFails", GateSide.Signed("TEAMAAAAAA"), GateSide.Signed("TEAMAAAAAA"), ProbeFailedReason, ProbeFails: true),
        new("UnsignedBundleIdMismatch", GateSide.Unsigned, GateSide.Unsigned, BundleIdReason,
            NewBundleId: "com.bundler.other"),
        new("SameTeamBundleIdMismatch", GateSide.Signed("TEAMAAAAAA"), GateSide.Signed("TEAMAAAAAA"), BundleIdReason,
            NewBundleId: "com.bundler.other"),
    ];

    public static IEnumerable<object[]> Data() => All.Select(c => new object[] { c });

    /// <summary>codesign -dv --verbose=4 的桩输出：(出口码, stderr)。</summary>
    internal static (int ExitCode, string StdErr) DescribeOutput(GateSide side, string identifier) =>
        side.Team is null
            ? (1, "code object is not signed at all\n")
            : (0, $"Executable=/stub\nIdentifier={identifier}\nFormat=app bundle with Mach-O thin (arm64)\n" +
                  $"TeamIdentifier={side.Team}\n");
}
