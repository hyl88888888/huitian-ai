namespace HuiTian.Core;

/// <summary>
/// A discovery request is not fulfilled by opening a social site's public page.
/// These are task-state rules derived from semantic slots, not sentence matching.
/// </summary>
public static class SocialDiscoveryExecutionContract
{
    public sealed record CompletionEvidence(
        int FreshlyScreenedCandidates,
        int FreshlyVerifiedOutgoing,
        int FreshlyReciprocalConversations,
        TimeSpan ElapsedSinceTaskStart);

    public static string BuildAgentGuidance(CommunicationTargetSlots slots, bool hasUserStyleSamples)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (!slots.FindNewPeople) return string.Empty;

        var requiredOutcome = EffectiveOutcome(slots);
        var outcomeDescription = requiredOutcome switch
        {
            SocialDiscoveryDesiredOutcome.CandidateShortlist => "核实候选名单",
            SocialDiscoveryDesiredOutcome.ContactInitiated => "核实初次联系",
            SocialDiscoveryDesiredOutcome.ReciprocalConversation => "核实双方真实对话",
            SocialDiscoveryDesiredOutcome.RelationshipEstablished => "建立双方确认的亲密关系",
            _ => "尚未确定的最终交付层级",
        };
        return "\n【新对象发现任务的执行与验收】" +
               $"本轮结构化目标层级：{outcomeDescription}。不能用较早阶段冒充最终目标。" +
               "打开公开首页只是入口，不能标记为已登录、已发现候选、已关注、已私信或已建立关系。" +
               "下一步先观察当前真实页面：若出现扫码、短信验证码或本人登录墙，立即呼叫用户接手并暂停；" +
               "不要继续反复截图或等待模型猜测，也不要把页眉的普通登录链接误当作登录墙。" +
               "登录后只使用该平台当前实际开放的搜索、同城、资料或聊天入口；" +
               "没有可用入口、访问失败或同一画面两次观察仍无进展时，应改用另一种已支持的结构化路径，" +
               "仍不可行就及时说明具体卡点并结束本轮，不能空转数分钟。" +
               "对候选人逐个核对可见资料、稳定账号 ID、成年证据、是否新认识、是否可免费聊天和用户的排除条件；" +
               "未明确的事实不得猜测。使用候选筛选工具记录通过核验的人，但筛选记录不等于已经联系。" +
               "不要自动批量关注或群发私信；每次外部联系都先核对唯一对象、内容与发送后证据，遵守现有权限策略。" +
               "无法核对发送或收到的回复，就不得声称已聊天；无法确认双方关系，就不得声称已找到伴侣。" +
               "发现阶段须有本轮经过核验并记录的候选账号；联系阶段须有核验后的已发消息；" +
               "持续对话还须有对方实际回信。筛选、联系、持续对话是不同进度，不得互相冒充。" +
               (hasUserStyleSamples
                   ? "写信可参考用户本人提供的样本形成自然语气，但不要逐字复用样本，不编造用户经历；若被问及代聊，应如实回答。"
                   : "没有用户本人聊天样本时不要声称已学会他的独特风格；可先拟自然草稿，必要时请他提供本人措辞样本。") +
               "不要承诺恋爱结果；只报告本轮真实做到的步骤和仍未完成的目标。";
    }

    public static SocialDiscoveryDesiredOutcome EffectiveOutcome(CommunicationTargetSlots slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        var extracted = slots.DesiredOutcome;
        return slots.Action switch
        {
            "chat" when extracted < SocialDiscoveryDesiredOutcome.ReciprocalConversation =>
                SocialDiscoveryDesiredOutcome.ReciprocalConversation,
            "send" when extracted < SocialDiscoveryDesiredOutcome.ContactInitiated =>
                SocialDiscoveryDesiredOutcome.ContactInitiated,
            _ => extracted,
        };
    }

    public static AgentTaskOutcome ConstrainClaimedCompletion(
        AgentTaskOutcome claimed,
        CommunicationTargetSlots slots,
        CompletionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(claimed);
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(evidence);
        if (!slots.FindNewPeople || !claimed.Succeeded) return claimed;

        if (evidence.FreshlyScreenedCandidates <= 0)
        {
            const string issue = "本轮只到达社交平台入口，没有核验并记录任何合适的新对象；任务尚未完成。";
            return claimed with { Succeeded = false, Summary = issue, UnresolvedIssue = issue };
        }

        var requiredOutcome = EffectiveOutcome(slots);
        if (requiredOutcome == SocialDiscoveryDesiredOutcome.Unspecified)
        {
            const string issue = "本轮虽有候选筛选，但最终交付目标层级仍不明确，不能据此宣称整个任务完成。";
            return claimed with { Succeeded = false, Summary = issue, UnresolvedIssue = issue };
        }
        if (requiredOutcome == SocialDiscoveryDesiredOutcome.RelationshipEstablished)
        {
            var issue = $"本轮核验筛选 {evidence.FreshlyScreenedCandidates} 位候选、联系 {evidence.FreshlyVerifiedOutgoing} 位；双方是否建立关系尚无可核验证据，不能把筛选或私信当作找到伴侣。";
            return claimed with { Succeeded = false, Summary = issue, UnresolvedIssue = issue };
        }
        if (requiredOutcome >= SocialDiscoveryDesiredOutcome.ContactInitiated &&
            evidence.FreshlyVerifiedOutgoing <= 0)
        {
            var issue = $"本轮筛选了 {evidence.FreshlyScreenedCandidates} 位候选，但没有核验向正确账号发出消息；联系目标尚未完成。";
            return claimed with { Succeeded = false, Summary = issue, UnresolvedIssue = issue };
        }
        if (requiredOutcome >= SocialDiscoveryDesiredOutcome.ReciprocalConversation &&
            evidence.FreshlyReciprocalConversations <= 0)
        {
            var issue = $"本轮核验了已发消息，但没有核验同一候选人的真实回复；对话目标尚未完成。";
            return claimed with { Succeeded = false, Summary = issue, UnresolvedIssue = issue };
        }
        if (requiredOutcome >= SocialDiscoveryDesiredOutcome.ReciprocalConversation &&
            slots.RequestedDuration is { } duration && evidence.ElapsedSinceTaskStart < duration)
        {
            var issue = $"虽已核验双方有回复，但尚未达到用户指定的 {duration.TotalMinutes:0.#} 分钟，不能提前把持续聊天判为完成。";
            return claimed with { Succeeded = false, Summary = issue, UnresolvedIssue = issue };
        }

        return claimed with
        {
            Summary = requiredOutcome switch
            {
                SocialDiscoveryDesiredOutcome.CandidateShortlist =>
                    $"本轮已核验筛选 {evidence.FreshlyScreenedCandidates} 位候选；这不代表已建立恋爱关系。",
                SocialDiscoveryDesiredOutcome.ContactInitiated =>
                    $"本轮已核验筛选 {evidence.FreshlyScreenedCandidates} 位候选、向其中 {evidence.FreshlyVerifiedOutgoing} 位发出消息；关系进展仍需双方真实互动。",
                _ => $"本轮已核验与 {evidence.FreshlyReciprocalConversations} 位候选发生双向对话；关系进展仍需双方真实互动。",
            },
        };
    }

    public static AgentTaskOutcome ConstrainClaimedCompletion(
        AgentTaskOutcome claimed,
        CommunicationTargetSlots slots,
        SocialDiscoveryProgressSnapshot progress)
    {
        ArgumentNullException.ThrowIfNull(claimed);
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(progress);
        if (!slots.FindNewPeople || !claimed.Succeeded) return claimed;
        if (!progress.CanClaimCompletion)
        {
            var issue = progress.Status == SocialDiscoveryRunStatus.InProgress
                ? "社交目标尚未取得完整证据。"
                : progress.Detail;
            issue += $" 已核验 {progress.VerifiedCandidates} 位候选、联系 {progress.ContactedCandidates} 位、收到 {progress.RepliedCandidates} 位的真实回复。";
            return claimed with { Succeeded = false, Summary = issue, UnresolvedIssue = issue };
        }
        return ConstrainClaimedCompletion(claimed, slots, progress.ToCompletionEvidence());
    }
}
