namespace MateMCP.Agent.Security;

public enum ComputerUseRiskLevel
{
    Low,
    Sensitive,
    High
}

public sealed record ComputerUseRiskAssessment(
    ComputerUseRiskLevel Level,
    string Effect,
    string Reason)
{
    public string Label => Level switch
    {
        ComputerUseRiskLevel.Low => "Low",
        ComputerUseRiskLevel.Sensitive => "Sensitive",
        ComputerUseRiskLevel.High => "High",
        _ => Level.ToString()
    };
}

/// <summary>
/// Deterministic, local risk hints for Computer Use approvals. This is intentionally
/// conservative and does not try to replace the AI/client's own reasoning. Semantic
/// labels improve the explanation; coordinate-only input remains uncertain.
/// </summary>
public static class ComputerUseRiskClassifier
{
    private static readonly string[] DestructiveTerms =
    [
        "delete", "remove", "revoke", "erase", "wipe", "destroy", "factory reset", "reset device", "drop", "uninstall",
        "حذف", "پاک کردن", "پاک کن", "ابطال", "لغو دسترسی", "بازنشانی", "ریست", "حذف نصب"
    ];

    private static readonly string[] FinancialTerms =
    [
        "pay", "payment", "purchase", "buy", "checkout", "place order", "confirm order", "transfer money", "send money",
        "پرداخت", "خرید", "ثبت سفارش", "انتقال وجه", "واریز", "تسویه"
    ];

    private static readonly string[] SecurityActionTerms =
    [
        "grant permission", "allow access", "revoke access", "change password", "reset password", "disable mfa", "disable 2fa",
        "enable admin", "make admin", "remove admin", "reset api key", "rotate api key",
        "اعطای مجوز", "اجازه دسترسی", "لغو دسترسی", "تغییر رمز", "بازنشانی رمز", "غیرفعال کردن احراز", "مدیر کردن", "حذف مدیر", "بازنشانی کلید"
    ];

    private static readonly string[] CredentialTerms =
    [
        "password", "passcode", "secret", "credential", "api key", "access token", "auth token",
        "رمز عبور", "گذرواژه", "رمز", "توکن", "کلید api", "کلید دسترسی", "اعتبارنامه"
    ];

    private static readonly string[] SensitiveMutationTerms =
    [
        "save", "submit", "send", "apply", "update", "change", "create", "invite", "upload", "download", "publish",
        "approve", "deny", "confirm", "install", "sign out", "log out", "enable", "disable", "turn on", "turn off",
        "ذخیره", "ثبت", "ارسال", "اعمال", "به روز", "به روزرسانی", "تغییر", "ایجاد", "دعوت", "بارگذاری", "آپلود",
        "دانلود", "انتشار", "تایید", "رد", "نصب", "خروج", "فعال", "غیرفعال"
    ];

    public static ComputerUseRiskAssessment AssessSemantic(
        string action,
        string? role = null,
        string? name = null,
        string? identifier = null)
    {
        action = Normalize(action);
        var context = Normalize(string.Join(' ', new[] { role, name, identifier }.Where(value => !string.IsNullOrWhiteSpace(value))));

        if (action is "view" or "snapshot" or "focus" or "scroll" or "expand" or "collapse" or "navigate" or "back" or "forward" or "reload")
            return Low("Navigates, focuses, or inspects UI without an obvious persistent external effect.", "The requested action is observational or navigational.");

        if (action is "value" or "fill" or "type")
        {
            if (ContainsAny(context, CredentialTerms))
                return High("May enter credential or secret material into an authentication/security field.", "The semantic target looks credential-sensitive.");
            if (ContainsAny(context, SensitiveMutationTerms))
                return Sensitive("May modify data in a field associated with a persistent or externally-visible operation.", "The semantic target contains a mutation-sensitive label.");
            return Low("Changes a non-protected field value without itself submitting the form or triggering an external operation.", "The semantic target does not look sensitive or destructive.");
        }

        if (ContainsAny(context, DestructiveTerms))
            return High("May permanently delete, remove, revoke, reset, or destroy data/access.", "The semantic target contains destructive intent.");
        if (ContainsAny(context, FinancialTerms))
            return High("May initiate or confirm a payment, purchase, order, or financial transfer.", "The semantic target contains financial intent.");
        if (ContainsAny(context, SecurityActionTerms))
            return High("May change credentials, permissions, administrative access, or security settings.", "The semantic target contains security-changing intent.");
        if (ContainsAny(context, SensitiveMutationTerms))
            return Sensitive("May submit, save, send, publish, install, or otherwise change persistent/external state.", "The semantic target contains a state-changing intent.");

        if (action is "press" or "shortcut" or "keyboard")
            return Sensitive("May trigger an application command whose effect cannot be determined from the key alone.", "Keyboard actions lack enough semantic target context to prove a low-risk effect.");

        return Low("Changes local UI state without an obvious destructive, financial, security, or external effect.", "No high-risk or sensitive semantic intent was detected.");
    }

    public static ComputerUseRiskAssessment AssessRaw(string action)
        => new(
            ComputerUseRiskLevel.Sensitive,
            "The target effect cannot be determined reliably from coordinate/raw input alone.",
            $"Raw {Normalize(action)} input has no semantic target context.");

    public static string PolicyTarget(string baseTarget, string action, string semanticTarget, ComputerUseRiskAssessment assessment)
    {
        var readable = Normalize(semanticTarget);
        if (readable.Length > 140) readable = readable[..140] + "…";
        return $"{baseTarget}:{assessment.Label.ToLowerInvariant()}:{Normalize(action)}:{readable}";
    }

    public static string FormatLegacySummary(string summary, ComputerUseRiskAssessment assessment)
        => $"{summary}\nRisk: {assessment.Label}\nExpected effect: {assessment.Effect}";

    private static ComputerUseRiskAssessment Low(string effect, string reason)
        => new(ComputerUseRiskLevel.Low, effect, reason);

    private static ComputerUseRiskAssessment Sensitive(string effect, string reason)
        => new(ComputerUseRiskLevel.Sensitive, effect, reason);

    private static ComputerUseRiskAssessment High(string effect, string reason)
        => new(ComputerUseRiskLevel.High, effect, reason);

    private static bool ContainsAny(string value, IEnumerable<string> terms)
    {
        var haystack = $" {Normalize(value)} ";
        return terms.Any(term => haystack.Contains($" {Normalize(term)} ", StringComparison.Ordinal));
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        value = value.Replace('ي', 'ی').Replace('ك', 'ک').Replace('\u200c', ' ');
        var builder = new System.Text.StringBuilder(value.Length + 8);
        char previous = '\0';
        foreach (var ch in value)
        {
            if (char.IsUpper(ch) && previous != '\0' && (char.IsLower(previous) || char.IsDigit(previous))) builder.Append(' ');
            builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
            previous = ch;
        }
        return string.Join(' ', builder.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
