namespace PrepaidTracker;

/// <summary>
/// The DESCO sign-in flow. Two steps, because DESCO's rebuilt portal needs an account
/// that only their website can create: send the user out to register, then collect the
/// credentials here.
///
/// All prompts go through Modal.* — never DisplayAlert, which is unstyleable iOS system
/// UI (see the note in Modal.cs).
/// </summary>
public static class DescoSignIn
{
    /// <summary>
    /// Make sure this meter has a usable sign-in, offering registration first for a
    /// user who has never used the new portal. Returns true when a session is live.
    /// </summary>
    public static async Task<bool> EnsureAsync(AppPage page, Meter meter)
    {
        if (DescoApi.HasCredential(meter.Account)) return true;

        // "Open portal" vs "I have an account". A user upgrading from the old build
        // has no idea DESCO changed anything, so the explanation comes first and the
        // registration link is the default action.
        var needsRegistration = await Modal.Confirm(page.Root,
            L.S("authRegisterTitle"), L.S("authRegisterFirst"),
            L.S("authOpenPortal"), L.S("authHaveAccount"));

        if (needsRegistration)
        {
            try { await Launcher.OpenAsync("https://prepaid.desco.org.bd/"); } catch { }
            // Don't prompt on top of the browser handoff. They'll come back and tap
            // the account row (or Sign in on the balance card) when they're ready.
            return false;
        }

        return await PromptAsync(page, meter);
    }

    /// <summary>
    /// Ask for email/phone and password, and try them. Also the re-auth path when a
    /// session expires, which is why it's callable on its own.
    /// </summary>
    public static async Task<bool> PromptAsync(AppPage page, Meter meter)
    {
        var user = await Modal.Prompt(page.Root, L.S("authTitle"), L.S("authUserHint"));
        if (string.IsNullOrWhiteSpace(user)) return false;

        var pass = await Modal.Prompt(page.Root, L.S("authTitle"), L.S("authPassHint"));
        if (string.IsNullOrWhiteSpace(pass)) return false;

        // A free Render-style cold start isn't in play here — this is DESCO's own
        // server — but a government portal on a bad mobile connection still deserves a
        // generous window before we call it unreachable.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        SessionState st;
        try { st = await DescoApi.SignInAsync(meter.Account, user!.Trim(), pass!, cts.Token); }
        catch { st = SessionState.Unreachable; }

        if (st == SessionState.Ok)
        {
            // A successful sign-in clears the once-a-day "session expired" guard, so a
            // second expiry the same day can still speak.
            Notifier.ClearSessionWarning(meter.Id);
            return true;
        }

        await Modal.Info(page.Root, L.S("authTitle"), st switch
        {
            SessionState.NeedsLogin => L.S("authRejected"),
            SessionState.Misconfigured => L.S("authPortalChanged"),
            _ => L.S("authUnreachable")
        });
        return false;
    }
}