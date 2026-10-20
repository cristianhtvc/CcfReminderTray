namespace CcfReminder.Core;

/// <summary>
/// 六个固定关注的会议身份，顺序即界面显示顺序。
/// Id 与上游 ccfddl 数据文件同名；显示名以界面为准（上游 nips → 界面 NeurIPS）。
/// 本目录只含身份信息，不含任何截止日期。
/// </summary>
public static class ConferenceCatalog
{
    public static readonly IReadOnlyList<ConferenceInfo> All = new[]
    {
        new ConferenceInfo("icml", "ICML", "International Conference on Machine Learning"),
        new ConferenceInfo("iclr", "ICLR", "International Conference on Learning Representations"),
        new ConferenceInfo("nips", "NeurIPS", "Conference on Neural Information Processing Systems"),
        new ConferenceInfo("aamas", "AAMAS", "International Conference on Autonomous Agents and Multiagent Systems"),
        new ConferenceInfo("aaai", "AAAI", "AAAI Conference on Artificial Intelligence"),
        new ConferenceInfo("dai", "DAI", "International Conference on Distributed Artificial Intelligence"),
    };
}
