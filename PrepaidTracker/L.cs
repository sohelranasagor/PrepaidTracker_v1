namespace PrepaidTracker;

/// <summary>Every user-facing string, English + Bangla. Store.Language picks one.</summary>
public static class L
{
    public static bool Bn => Store.Language == "bn";

    static readonly Dictionary<string, (string En, string Bn)> T = new()
    {
        ["app"] = ("Prepaid Tracker", "প্রিপেইড ট্র্যাকার"),
        ["tagline"] = ("Prepaid meter management", "প্রিপেইড মিটার ব্যবস্থাপনা"),
        ["home"] = ("Home", "হোম"),
        ["usage"] = ("Usage", "হিসাব"),
        ["insights"] = ("Insights", "পরামর্শ"),
        ["profile"] = ("Profile", "প্রোফাইল"),

        // onboarding
        ["heroTitle"] = ("Your meter,\nin one place.", "আপনার মিটার,\nএক জায়গায়।"),
        ["heroSub"] = ("Balance, usage, recharge — all in one tap.", "ব্যালেন্স, ব্যবহার, রিচার্জ — সবকিছু এক ট্যাপে।"),
        ["chooseUtility"] = ("Choose your utility", "বিদ্যুৎ কোম্পানি নির্বাচন করুন"),
        ["enterAccount"] = ("Enter your account number", "অ্যাকাউন্ট নম্বর দিন"),
        ["enterAccountSub"] = ("Balance and usage straight from the utility — in seconds.",
                              "ব্যালেন্স ও ব্যবহার সরাসরি বিদ্যুৎ কোম্পানি থেকে — সেকেন্ডেই।"),
        ["egAccount"] = ("e.g. 32011435", "যেমন: ৩২০১১৪৩৫"),
        ["invalidAccount"] = ("Enter a valid number", "সঠিক নম্বর দিন"),
        ["continue"] = ("Continue", "এগিয়ে যান"),
        ["trustRow"] = ("Private · Official data · Real-time", "নিরাপদ · অফিসিয়াল ডেটা · রিয়েল-টাইম"),

        // ---- DESCO sign-in (see DescoApi.cs) ----
        // The portal was rebuilt with authentication in Nov 2026; DESCO's own notice says
        // it was added to protect customer data. An account number alone no longer opens
        // anything, so DESCO onboarding is now two steps: register on their portal, then
        // sign in here.
        ["authTitle"] = ("Sign in to DESCO", "ডেসকোতে সাইন ইন করুন"),
        ["authUserHint"] = ("Email or phone", "ইমেইল বা ফোন"),
        ["authPassHint"] = ("Password", "পাসওয়ার্ড"),
        ["authRegisterTitle"] = ("DESCO account needed", "ডেসকো অ্যাকাউন্ট প্রয়োজন"),
        // Says WHY, because a user who last used this app when an account number was
        // enough will otherwise think the app broke. The change is DESCO's, not ours.
        ["authRegisterFirst"] = ("DESCO's portal now requires an account. Create one on their website, verify it, then sign in here.",
                                 "ডেসকোর পোর্টালে এখন অ্যাকাউন্ট লাগে। তাদের ওয়েবসাইটে অ্যাকাউন্ট খুলে যাচাই করুন, তারপর এখানে সাইন ইন করুন।"),
        ["authOpenPortal"] = ("Open DESCO portal", "ডেসকো পোর্টাল খুলুন"),
        ["authHaveAccount"] = ("I have an account", "আমার অ্যাকাউন্ট আছে"),
        ["authRejected"] = ("Wrong email/phone or password.", "ইমেইল/ফোন বা পাসওয়ার্ড ভুল।"),
        ["authUnreachable"] = ("Couldn't reach DESCO. Try again in a moment.",
                               "ডেসকোতে সংযোগ করা যায়নি। একটু পর আবার চেষ্টা করুন।"),
        // Distinct from "wrong password" on purpose: this one is OUR problem, not the
        // user's, and no amount of retyping will fix it. It means the portal changed
        // shape under us and the app needs an update.
        ["authPortalChanged"] = ("DESCO's portal has changed and the app can't sign in. An app update is needed.",
                                 "ডেসকোর পোর্টাল পরিবর্তিত হয়েছে, অ্যাপ সাইন ইন করতে পারছে না। অ্যাপ আপডেট প্রয়োজন।"),
        ["authExpiredTitle"] = ("Sign in to DESCO again", "ডেসকোতে আবার সাইন ইন করুন"),
        // Inline form, used as a fetch error string by DescoProvider.
        ["authExpiredBody"] = ("Your DESCO session expired. Open the app and sign in again.",
                               "ডেসকো সেশনের মেয়াদ শেষ। অ্যাপ খুলে আবার সাইন ইন করুন।"),
        // Notification form, which leads with the account so a two-meter user knows
        // which one needs attention. {0} = Notifier.Lead.
        ["authExpiredBody2"] = ("{0} • Session expired • Open the app and sign in again",
                               "{0} • সেশনের মেয়াদ শেষ • অ্যাপ খুলে আবার সাইন ইন করুন"),
        ["authSignedIn"] = ("Signed in", "সাইন ইন হয়েছে"),
        ["authSignOut"] = ("Sign out of DESCO", "ডেসকো থেকে সাইন আউট"),
        ["authSignOutQ"] = ("Sign out of DESCO? You'll need your password to sync this meter again.",
                               "ডেসকো থেকে সাইন আউট করবেন? এই মিটার আবার সিঙ্ক করতে পাসওয়ার্ড লাগবে।"),
        ["authNeeded"] = ("Sign in to see this meter's balance.",
                               "এই মিটারের ব্যালেন্স দেখতে সাইন ইন করুন।"),

        // ---- the live-read limit ----
        // DESCO allows one successful fresh meter reading per meter per day. Everything
        // else the app shows comes from their cached figure and costs nothing, so this
        // only ever appears on a manual refresh. Same wording as DESCO's own page.
        ["refresh"] = ("Refresh", "রিফ্রেশ"),
        ["quotaTitle"] = ("Realtime balance", "রিয়েলটাইম ব্যালেন্স"),
        ["quotaReachedMeter"] = ("Today's realtime balance request limit for meter {0} has been reached.\nYou can request another reading after {1}.",
                                 "মিটার {0}-এর আজকের রিয়েলটাইম ব্যালেন্স অনুরোধের সীমা শেষ হয়েছে।\n{1}-এর পর আবার রিডিং নিতে পারবেন।"),
        // A refresh the user asked for that produced no new balance for any reason OTHER
        // than the daily limit. {0} is DESCO's own status — "HTTP 500 …", "timeout",
        // "FAILED …" — left in Latin on purpose: it is what the developer needs to see
        // in a screenshot, and translating it would hide the one useful part.
        ["freshFailed"] = ("DESCO didn't return a new reading ({0}). Showing the last saved balance.",
                           "ডেসকো নতুন রিডিং দেয়নি ({0})। সর্বশেষ সংরক্ষিত ব্যালেন্স দেখানো হচ্ছে।"),

        // home
        ["welcome"] = ("Welcome", "স্বাগতম"),
        ["currentBalance"] = ("Current balance", "বর্তমান ব্যালেন্স"),
        ["willLast"] = ("{0} days left", "{0} দিন চলবে"),
        ["willLast1"] = ("1 day left", "১ দিন চলবে"),
        ["recharge"] = ("Recharge", "রিচার্জ"),
        ["details"] = ("Details", "বিস্তারিত"),
        ["advice"] = ("Advice", "পরামর্শ"),
        ["monthSummary"] = ("This month", "এ মাসের সারাংশ"),
        ["spentThisMonth"] = ("Spent this month", "এ মাসে খরচ"),
        ["spentThisMonthEst"] = ("Est. spent this month", "আনুমানিক এ মাসে খরচ"),
        ["dailyAvg"] = ("Daily average", "দৈনিক গড়"),
        ["estBill"] = ("Estimated bill", "আনুমানিক বিল"),
        ["sevenDaySpend"] = ("7-day spend", "৭ দিনের খরচ"),
        ["monthlySpend"] = ("Monthly spend", "মাসিক খরচ"),
        ["spentLastMonth"] = ("Spent last month", "গত মাসে খরচ"),
        ["latest"] = ("Latest", "সর্বশেষ"),
        ["seeAll"] = ("See all", "সব দেখুন"),
        ["today"] = ("Today", "আজ"),
        ["lastFullDay"] = ("Last full day", "সর্বশেষ পূর্ণ দিন"),
        ["average"] = ("Average", "গড়"),
        ["highest"] = ("Highest", "সর্বোচ্চ"),
        ["recentRecharge"] = ("Recent recharges", "সাম্প্রতিক রিচার্জ"),
        // The old text ended with "Emergency credit: 99999" — a placeholder that was
        // never replaced, so the app confidently told every user a made-up number.
        // Say only what we actually know.
        ["lowWarning"] = ("Low balance — recharge soon.",
                              "ব্যালেন্স কম — শীঘ্রই রিচার্জ করুন।"),
        ["negWarning"] = ("Balance is negative — the meter is running on emergency credit. Recharge now to avoid a cut-off.",
                              "ব্যালেন্স ঋণাত্মক — মিটার জরুরি ব্যালেন্সে চলছে। সংযোগ বিচ্ছিন্ন হওয়ার আগে এখনই রিচার্জ করুন।"),
        ["rechargeNow"] = ("Recharge now", "এখনই রিচার্জ করুন"),

        // Appended to a warning ONLY when the portal actually published the figure.
        // {0} is filled from Snapshot.EmergencyBalance / MinRecharge — never a literal.
        ["emergencyCredit"] = ("Emergency credit: ৳{0}.", "জরুরি ব্যালেন্স: ৳{0}।"),
        ["minRecharge"] = ("Minimum recharge: ৳{0}.", "সর্বনিম্ন রিচার্জ: ৳{0}।"),
        ["staleNote"] = ("The utility reads the meter about once a day, so this can lag.",
                              "কোম্পানি দিনে একবার মিটার পড়ে, তাই এটি কিছুটা পুরোনো হতে পারে।"),

        // access control (see Access.cs)
        ["usernameTitle"] = ("Choose a username", "একটি ইউজারনেম বাছুন"),
        ["usernameSub"] = ("This name identifies your device to the app owner. Pick one and keep it \u2014 it can't be changed later.",
                               "এই নামে অ্যাপের মালিক আপনার ডিভাইস চিনবেন। একবার বাছলে পরে বদলানো যাবে না।"),
        ["usernamePlaceholder"] = ("e.g. john_doe", "যেমন john_doe"),
        ["usernameHint"] = ("3\u201320 characters: lowercase letters, numbers, underscore.",
                               "৩–২০ অক্ষর: ছোট হাতের ইংরেজি অক্ষর, সংখ্যা, আন্ডারস্কোর।"),
        ["usernameTaken"] = ("That username is already taken.", "এই ইউজারনেম আগেই নেওয়া হয়েছে।"),
        ["usernameInvalid"] = ("Use 3\u201320 lowercase letters, numbers or underscore.",
                               "৩–২০টি ছোট হাতের অক্ষর, সংখ্যা বা আন্ডারস্কোর ব্যবহার করুন।"),
        ["usernameOffline"] = ("Couldn't reach the server. Check your connection and try again.",
                               "সার্ভারে পৌঁছানো যায়নি। সংযোগ দেখে আবার চেষ্টা করুন।"),

        // The wall a blocked device hits. Kept plain: the person holding the phone is not
        // the one who did anything wrong, and has no idea who "the owner" is.
        ["blockedTitle"] = ("Access removed", "অ্যাক্সেস বাতিল"),
        ["blockedBody"] = ("The owner has blocked this device. Your customer list has been removed, and the app can't be used until access is restored.",
                               "মালিক এই ডিভাইসটি ব্লক করেছেন। আপনার গ্রাহক তালিকা মুছে ফেলা হয়েছে, এবং অ্যাক্সেস ফিরে না পাওয়া পর্যন্ত অ্যাপটি ব্যবহার করা যাবে না।"),
        ["wakingServer"] = ("Waking the server \u2014 the first check of the day can take a minute.",
                               "সার্ভার চালু হচ্ছে — দিনের প্রথমবার এক মিনিট সময় নিতে পারে।"),
        ["checkAgain"] = ("Check again", "আবার দেখুন"),
        ["checking"] = ("Checking\u2026", "দেখা হচ্ছে…"),
        ["stillBlocked"] = ("Still blocked.", "এখনও ব্লক করা আছে।"),

        // usage
        ["usageTitle"] = ("Usage", "ব্যবহারের হিসাব"),
        ["7days"] = ("7 days", "৭ দিন"),
        ["30days"] = ("30 days", "৩০ দিন"),
        ["year"] = ("Year", "বছর"),
        ["totalSpend"] = ("Total spend", "মোট খরচ"),
        ["highestDay"] = ("Highest day", "সর্বোচ্চ দিন"),
        ["rechargeHistory"] = ("Recharge history", "রিচার্জ ইতিহাস"),
        ["success"] = ("Success", "সফল"),
        ["failed"] = ("Failed", "ব্যর্থ"),

        // ---- failed-recharge reconciliation (see Reconcile.cs) ----
        // THREE states, not two. "Verifying" is the honest label for the window where the
        // portal has said failed but no meter read has passed the recharge yet — during
        // which "enter the token manually" would be a guess presented as a fact.
        ["verifying"] = ("Verifying", "যাচাই হচ্ছে"),
        ["reachedMeter"] = ("Reached meter", "মিটারে পৌঁছেছে"),
        ["addToken"] = ("Add token", "টোকেন দিন"),
        ["tokenNotApplied"] = ("Enter the token manually",
                              "টোকেন ম্যানুয়ালি প্রবেশ করান"),
        ["tokenNotAppliedSub"] = ("The ৳{0} recharge never reached the meter. Type this token on the keypad.",
                                  "৳{0} রিচার্জটি মিটারে পৌঁছায়নি। কীপ্যাডে এই টোকেনটি লিখুন।"),
        ["tokenFindInDetails"] = ("The utility published no token — tap for the full transaction details.",
                                  "বিদ্যুৎ কোম্পানি টোকেন দেয়নি — বিস্তারিত দেখতে ট্যাপ করুন।"),
        ["tokenEntered"] = ("I've entered it", "প্রবেশ করিয়েছি"),
        ["tokenAlertTitle"] = ("Recharge didn't reach the meter",
                              "রিচার্জ মিটারে পৌঁছায়নি"),
        ["tokenAlertBody"] = ("{0} — the ৳{1} recharge was not credited. Open the app and enter the token on your meter.",
                              "{0} — ৳{1} রিচার্জ জমা হয়নি। অ্যাপ খুলে মিটারে টোকেনটি দিন।"),
        // A meter key-change arrives as ONE transaction carrying a SET of tokens (twelve,
        // in the captured data). It isn't a recharge and can't be reconciled, so the app
        // makes no claim about it — it just hands over every token, in order.
        ["tokenSetTitle"] = ("Meter key change — {0} tokens",
                              "মিটার কী পরিবর্তন — {0}টি টোকেন"),
        ["tokenSetSub"] = ("Enter all of them on the keypad, in this order.",
                              "কীপ্যাডে সবগুলো এই ক্রমেই দিন।"),
        // Sits under the balance while the estimate is carrying money we can't yet stand
        // behind. Kept short: it shares a narrow card with the meter-read caption.
        ["estProvisional"] = ("incl. ৳{0} unverified",
                              "৳{0} অযাচাইকৃত ধরা আছে"),
        // Was NESCO-specific. DESCO's rebuilt portal exposes no daily endpoint either, so
        // both utilities now land here and naming one of them would be wrong half the time.
        ["noDaily"] = ("The utility publishes no daily feed. Sync each day and this chart fills in.",
                              "বিদ্যুৎ কোম্পানি দৈনিক তথ্য দেয় না। প্রতিদিন সিঙ্ক করলে এই চার্ট তৈরি হবে।"),
        ["monthlyInstead"] = ("No daily data — showing monthly totals instead.",
                              "দৈনিক তথ্য নেই — মাসিক মোট দেখানো হচ্ছে।"),
        ["noRecharges"] = ("No recharges returned by the utility yet.",
                              "বিদ্যুৎ কোম্পানি এখনো কোনো রিচার্জ তথ্য দেয়নি।"),
        ["seeMore"] = ("See more", "আরো দেখুন"),
        ["seeLess"] = ("See less", "কম দেখুন"),
        ["electricity"] = ("Electricity", "বিদ্যুৎ"),
        ["rechargeDetails"] = ("Recharge details", "রিচার্জ বিস্তারিত"),
        ["close"] = ("Close", "বন্ধ করুন"),
        ["balanceUnavailable"] = ("DESCO reports no balance for this meter — showing usage and recharges instead.",
                                  "ডেসকো এই মিটারের ব্যালেন্স দেয় না — ব্যবহার ও রিচার্জ দেখানো হচ্ছে।"),
        ["dailyRate"] = ("~ ৳{0} a day", "দৈনিক ~ ৳{0}"),
        ["meterReadAt"] = ("Meter read: {0}", "মিটার রিডিং: {0}"),
        ["updateCount"] = ("{0} updates so far", "এ পর্যন্ত {0} বার আপডেট"),
        ["updateCount1"] = ("1 update so far", "এ পর্যন্ত ১ বার আপডেট"),
        // Sub-labels for the adaptive split on the "দৈনিক গড় / Daily average" cell.
        ["avgAllDays"] = ("all days", "সব দিন"),
        ["avgActive"] = ("active days", "সক্রিয় দিন"),
        // Compact caption sharing a row with "মিটার রিডিং: …". The "≈" symbol and
        // the tag word "আনুমানিক/estimate" together signal projection.
        ["liveEstimate"] = ("≈ ৳{0} now (est.)", "≈ ৳{0} এখন (আনুমানিক)"),
        ["liveEstimateInline"] = ("≈ ৳{0} now (est.)", "≈ ৳{0} এখন (আনুমানিক)"),
        // Negative form — minus sign sits BEFORE the ৳ symbol, mirroring how the
        // big balance number renders "-৳50.00".
        ["liveEstimateInlineNeg"] = ("≈ -৳{0} now (est.)", "≈ -৳{0} এখন (আনুমানিক)"),
        ["swipeToDelete"] = ("Swipe an account to delete it.", "মুছতে অ্যাকাউন্টে সোয়াইপ করুন।"),

        // insights
        ["insightsTitle"] = ("Insights", "পরামর্শ"),
        ["modelBadge"] = ("From your usage", "আপনার ব্যবহার থেকে"),
        ["basedOnAvg"] = ("Based on your average usage", "আপনার গড় ব্যবহারের ভিত্তিতে"),
        ["suggestedRech"] = ("Suggested recharge", "প্রস্তাবিত রিচার্জ"),
        ["enoughFor"] = ("Enough for {0} days", "{0} দিনের জন্য যথেষ্ট"),
        // Shown under the suggested amount on the first recharge of a month, when the
        // sanction load is known. The demand charge is a real deduction the customer
        // would otherwise think the app got wrong by ৳168.
        ["inclDemand"] = ("includes ৳{0} demand charge", "৳{0} ডিমান্ড চার্জসহ"),
        ["sevenDayAvg"] = ("7-day average", "৭ দিনের গড়"),
        ["monthEnd"] = ("Month end", "মাস শেষে"),
        ["personalAnalysis"] = ("Personal analysis", "ব্যক্তিগত বিশ্লেষণ"),
        ["savingPct"] = ("Spending {0}% less than usual this month", "চলতি মাসে গড়ের চেয়ে {0}% সাশ্রয় হচ্ছে"),
        ["overPct"] = ("Spending {0}% more than usual this month", "চলতি মাসে গড়ের চেয়ে {0}% বেশি খরচ হচ্ছে"),
        ["todayBelow"] = ("Today's spend ৳{0} — below average", "আজকের খরচ ৳{0} — গড়ের চেয়ে কম"),
        ["todayAbove"] = ("Today's spend ৳{0} — above average", "আজকের খরচ ৳{0} — গড়ের চেয়ে বেশি"),
        ["setReminder"] = ("Set a recharge reminder", "রিচার্জ রিমাইন্ডার সেট করুন"),
        ["needMoreData"] = ("A day or two more of data and the forecast begins.",
                              "আর এক-দুই দিনের তথ্য হলেই পূর্বাভাস শুরু হবে।"),
        ["confidenceLow"] = ("Estimate improves with more history", "আরও তথ্য জমলে হিসাব আরও নির্ভুল হবে"),

        // profile / settings
        ["yourAccounts"] = ("Your accounts", "আপনার অ্যাকাউন্ট"),
        ["active"] = ("Active", "চলমান"),
        ["addAccount"] = ("Add another account", "আরেকটি অ্যাকাউন্ট যোগ করুন"),
        ["settings"] = ("Settings", "সেটিংস"),
        ["language"] = ("Language", "ভাষা"),
        ["notification"] = ("Notification", "নোটিফিকেশন"),
        ["alertAt"] = ("Alert below ৳{0}", "৳{0} এর নিচে জানাবে"),
        ["alertAtDays"] = ("or below {0} days left", "অথবা {0} দিন বাকি থাকলে"),
        ["notifAllOn"] = ("All notifications", "সব নোটিফিকেশন"),
        ["notifAllHint"] = ("Master switch for every account.", "সব অ্যাকাউন্টের জন্য মাস্টার সুইচ।"),
        ["notifPerAccount"] = ("Per account", "অ্যাকাউন্ট অনুযায়ী"),
        ["notifPerHint"] = ("Tap a row to set its own ৳ and days limits, or use the switch to mute it.",
                              "রো ট্যাপ করে ৳ এবং দিনের সীমা দিন, অথবা সুইচ দিয়ে বন্ধ রাখুন।"),
        ["alertBelow"] = ("Alert below", "নিচে জানাবে"),
        ["orBelow"] = ("or below", "অথবা নিচে"),
        // Prompt-dialog variants: the inline editor labels above sit next to a "৳" or
        // "days left" value so the unit is implicit, but a bare Prompt dialog loses
        // that context.
        ["alertBelowTakaPrompt"] = ("Alert when balance drops below (৳)",
                                    "ব্যালেন্স কত টাকার নিচে নামলে জানাবে (৳)"),
        ["alertBelowDaysPrompt"] = ("Or when days left drops below (1–30)",
                                    "অথবা কত দিন বাকি থাকলে জানাবে (১–৩০)"),
        ["daysLeft"] = ("days left", "দিন বাকি"),
        ["done"] = ("Done", "সম্পন্ন"),
        ["tariff"] = ("Tariff — ৳ per unit", "ট্যারিফ — প্রতি ইউনিট ৳"),
        ["tariffHint"] = ("Only used to estimate kWh. Deductions also include demand charge, meter rent and VAT.",
                              "শুধু ইউনিট অনুমানের জন্য। কাটা টাকায় ডিমান্ড চার্জ, মিটার ভাড়া ও ভ্যাটও থাকে।"),
        ["targetDays"] = ("Recharge should cover {0} days", "রিচার্জ {0} দিন চলবে এমন হবে"),
        ["diagnostics"] = ("Diagnostics", "ডায়াগনস্টিকস"),
        ["diagHint"] = ("Which endpoints answered on the last sync, and the state of the DESCO sign-in.",
                              "গত সিঙ্কে কোন এন্ডপয়েন্ট সাড়া দিয়েছে, আর ডেসকো সাইন ইনের অবস্থা।"),
        ["copyDiag"] = ("Copy diagnostics", "ডায়াগনস্টিকস কপি করুন"),
        ["copied"] = ("Copied", "কপি হয়েছে"),
        ["bgSync"] = ("Background refresh", "ব্যাকগ্রাউন্ড রিফ্রেশ"),
        ["bgHintAndroid"] = ("Android wakes the app about once a day to check your balance. Timing is up to Android, and some phones (Xiaomi, Oppo, Vivo) block it entirely — so the recharge reminder is also scheduled ahead of time, and reaches you even if the app never runs.",
                              "অ্যান্ড্রয়েড দিনে একবারের মতো অ্যাপ চালু করে ব্যালেন্স দেখে। সময় অ্যান্ড্রয়েড ঠিক করে, আর কিছু ফোন (শাওমি, অপো, ভিভো) এটি বন্ধ রাখে — তাই রিচার্জ রিমাইন্ডার আগেই সেট করা থাকে, অ্যাপ না চললেও তা পৌঁছাবে।"),
        ["privacyTitle"] = ("Privacy", "গোপনীয়তা"),
        ["bgHint"] = ("iOS wakes the app on its own schedule to check your balance. Keep Background App Refresh on, and don't force-quit the app — a force-quit app gets no wakes. The recharge reminder is queued ahead of time, so it arrives either way.",
                              "আইওএস নিজের সময়মতো অ্যাপ চালু করে ব্যালেন্স দেখে। Background App Refresh চালু রাখুন, আর অ্যাপটি সোয়াইপ করে বন্ধ করবেন না — বন্ধ করা অ্যাপ চালু হয় না। রিচার্জ রিমাইন্ডার আগেই সেট করা থাকে, তাই সেটি যেভাবেই হোক পৌঁছাবে।"),
        // REWRITTEN. The old text — "Your meter number never leaves this phone" — became
        // false the day DESCO required a login: the app now sends an email/phone and a
        // password to their portal, and stores both on the device. A privacy promise that
        // is no longer true is worse than no promise, so this says exactly what happens.
        ["privacy"] = ("Your DESCO sign-in goes only to DESCO's own portal and is stored encrypted on this phone. Nothing is sent anywhere else — no analytics, and no balances or names leave the device.",
                              "আপনার ডেসকো সাইন ইন শুধু ডেসকোর নিজস্ব পোর্টালে যায় এবং এই ফোনে এনক্রিপ্ট করে রাখা হয়। আর কোথাও কিছু যায় না — কোনো অ্যানালিটিক্স নেই, ব্যালেন্স বা নামও ফোনের বাইরে যায় না।"),
        ["unofficial"] = ("Unofficial app. Not affiliated with NESCO or DESCO.",
                              "বেসরকারি অ্যাপ। নেসকো বা ডেসকোর সাথে সম্পৃক্ত নয়।"),
        ["label"] = ("Label (Home, Shop…)", "নাম (বাসা, দোকান…)"),
        ["delete"] = ("Delete", "মুছুন"),
        ["cancel"] = ("Cancel", "বাতিল"),
        ["save"] = ("Save", "সংরক্ষণ"),
        ["deleteMeterQ"] = ("Delete this account and its history?", "এই অ্যাকাউন্ট ও এর হিস্ট্রি মুছে ফেলবেন?"),
        ["addReading"] = ("Add a reading by hand", "নিজে রিডিং যোগ করুন"),
        ["balancePrompt"] = ("Current meter balance in Taka (dial 037 on the meter)",
                              "মিটারের বর্তমান ব্যালেন্স টাকায় (মিটারে ০৩৭ চাপুন)"),
        ["rechargedSince"] = ("Recharged since last reading (৳)", "গত রিডিংয়ের পর রিচার্জ (৳)"),

        // status
        ["syncing"] = ("Syncing…", "সিঙ্ক হচ্ছে…"),
        ["offline"] = ("Offline — showing your last saved balance.", "অফলাইন — সর্বশেষ সংরক্ষিত ব্যালেন্স।"),
        ["justNow"] = ("just now", "এইমাত্র"),
        ["minAgo"] = ("{0} min ago", "{0} মিনিট আগে"),
        ["hrAgo"] = ("{0} h ago", "{0} ঘন্টা আগে"),
        ["monthlyNote"] = ("NESCO reports monthly, not daily. The rate is worked out from the latest full month.",
                              "নেসকো মাসিক তথ্য দেয়, দৈনিক নয়। সর্বশেষ পূর্ণ মাস থেকে হার হিসাব করা হয়েছে।"),
        // REWORDED. This used to describe a transient portal glitch — the daily feed
        // answered with something unusable. On the rebuilt portal there is no daily
        // endpoint at all, so "unavailable this sync" would imply it might come back
        // tomorrow. It won't, and the honest version tells the user what to do instead.
        ["monthlyNoteFallback"] = ("DESCO's new portal publishes no daily figures — the rate comes from the monthly total, and sharpens as the app records your balance each day.",
                              "ডেসকোর নতুন পোর্টাল দৈনিক তথ্য দেয় না — মাসিক মোট থেকে হার হিসাব হচ্ছে, আর প্রতিদিন ব্যালেন্স রেকর্ড হতে হতে এটি আরও নির্ভুল হবে।"),
        ["descoNoData"] = ("The utility hasn't returned usable data yet — try syncing again in a minute.",
                              "বিদ্যুৎ কোম্পানি থেকে এখনো ব্যবহারযোগ্য তথ্য আসেনি — এক মিনিট পর আবার সিঙ্ক করুন।"),
        ["noDataYet"] = ("No usage data yet", "এখনো ব্যবহারের তথ্য নেই"),
        ["noMeters"] = ("Add a meter to get started.", "শুরু করতে একটি মিটার যোগ করুন।"),
        ["yearTab"] = ("Monthly usage is available under Year.", "মাসিক ব্যবহার “বছর” ট্যাবে দেখুন।"),
        ["alertTitle"] = ("Recharge your meter", "মিটার রিচার্জ করুন"),
        // Immediate alert body — verbose, natural-sounding. The lead line ({0} = A/C
        // + name via Notifier.Lead) stays the same; higher-level clauses join with `•`
        // (U+2022) so they read as distinct thoughts rather than a comma-list.
        ["alertBody"] = ("{0} • Remaining balance: ৳{1} • Approximately {2} days left",
                              "{0} • অবশিষ্ট ব্যালেন্স: ৳{1} টাকা • আনুমানিক {2} দিন চলবে"),
        // The queued alert. Its date is computed at sync time and handed to the OS, so it
        // fires on the day even if the app never wakes again — which means it cannot quote
        // a live balance. It leads with the account, then says what it knew.
        ["alertPredicted"] = ("{0} • Balance is likely low now • Open the app to check",
                              "{0} • ব্যালেন্স সম্ভবত এখন কম • অ্যাপ খুলে দেখুন"),
        ["notifBlockedTitle"] = ("Notifications are off", "নোটিফিকেশন বন্ধ আছে"),
        ["notifBlockedBody"] = ("Alerts are on in the app, but your phone is blocking notifications for it. Turn them on in your phone's Settings to receive low-balance warnings.",
                                 "অ্যাপে অ্যালার্ট চালু আছে, কিন্তু ফোন এই অ্যাপের নোটিফিকেশন ব্লক করে রেখেছে। কম-ব্যালেন্স সতর্কতা পেতে ফোনের সেটিংসে গিয়ে চালু করুন।"),
    };

    public static string S(string key) => T.TryGetValue(key, out var v) ? (Bn ? v.Bn : v.En) : key;
    public static string S(string key, params object[] args) => string.Format(S(key), args);

    /// <summary>
    /// Numbers stay in Latin digits, in both languages. An account number in
    /// Bengali numerals can't be checked against the one printed on the meter,
    /// and money is read faster in the digits people type. Words get translated;
    /// digits don't.
    /// </summary>
    public static string N(string ascii) => ascii;

    /// <summary>
    /// Prose-context number: converts ASCII digits to Bengali numerals when the
    /// UI language is Bengali. Use for numbers that read as words in a sentence —
    /// notification bodies especially.
    ///
    /// NOT used for numbers the user types back or compares to something printed
    /// in Latin — account numbers, PINs, meter numbers, STS TOKENS, and the balance
    /// on the balance card itself all stay in Latin via <see cref="N"/>. The
    /// distinction is "is this a word in a sentence, or a token I might dial into a
    /// meter?" A token in Bengali numerals is unkeyable on a meter keypad.
    /// </summary>
    public static string NBn(string ascii)
    {
        if (!Bn || string.IsNullOrEmpty(ascii)) return ascii;
        var sb = new System.Text.StringBuilder(ascii.Length);
        foreach (var c in ascii)
        {
            if (c >= '0' && c <= '9') sb.Append(BengaliDigits[c - '0']);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    static readonly string[] BengaliDigits = { "০", "১", "২", "৩", "৪", "৫", "৬", "৭", "৮", "৯" };

    /// <summary>"15:10" → "3:10 PM". Anything unparseable comes back untouched.</summary>
    public static string T12(string hhmm)
    {
        var parts = hhmm.Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m))
            return hhmm;
        var pm = h >= 12;
        var h12 = h % 12; if (h12 == 0) h12 = 12;
        return $"{h12}:{m:00} {(pm ? "PM" : "AM")}";
    }
}