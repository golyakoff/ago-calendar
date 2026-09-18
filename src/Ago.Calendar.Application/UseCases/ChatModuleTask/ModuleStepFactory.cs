using System.Globalization;
using Ago.Calendar.Application.Abstractions;
using Ago.Calendar.Domain;

namespace Ago.Calendar.Application.UseCases.ChatModuleTask;

/// <summary>
/// Turns the existing `PublicBooking` read rows into <see cref="ModuleStep"/>s. Shared by
/// <c>StartModuleTaskHandler</c> and <c>ReplyToModuleTaskHandler</c> so the mapping from, say, a
/// <see cref="BookableWorkerRow"/> to a choice action is written once - the same reason
/// <c>EmbedScopeResolver</c> is one class rather than three copies of a preamble.
///
/// <para><b>`25-37`: every prompt takes a <c>locale</c> string.</b> This module boundary carried no
/// locale at all before this item - Chat's own wire request now resends the site's own configured
/// language (its Domain enum's PascalCase member name, <c>"En"</c>/<c>"Ru"</c>) on every call, and
/// this factory is the one place that turns it into text, exactly the way it is already the one place
/// that turns a read row into a prompt. A plain string rather than a new Calendar-side enum: this
/// value already crosses a hand-synchronized wire boundary as text (like <c>Kind</c> elsewhere in this
/// same contract), and inventing a closed-vocabulary type here - used only to pick between two
/// hardcoded string tables in one internal static class - would be exactly the premature
/// generalisation `clean-architecture.md` warns a platform/product layer against. Anything this
/// factory does not recognise (a genuinely unconfigured value, or a locale Chat knows about that this
/// module has no strings for yet) renders in English, the same "never guess, default to the safe,
/// always-available choice" posture <c>WidgetConfig.Default</c> already takes for its own unset
/// fields.</para>
/// </summary>
internal static class ModuleStepFactory
{
    /// <summary>`25-33`: how many distinct upcoming dates the date round offers. Ten - the exact
    /// reasoning the flat, pre-`25-33` slot list gave its own page bound ("a text channel printing a
    /// numbered list ... is not a menu, it is a wall of text"), now applied to <i>days</i> rather than
    /// raw slots, since a day is what a visitor is actually choosing between at this step. See
    /// <see cref="TimeSlotPageSize"/>'s own remarks for how the same reasoning's *target* changed for
    /// the round after this one.</summary>
    private const int DatePageSize = 10;

    /// <summary>`25-33`: how many times within one already-chosen date the time round offers. Ten
    /// again - deliberately the identical number <see cref="DatePageSize"/> uses, but the backlog
    /// item's own instruction was to revisit the *reasoning*, not assume it carries over unchanged,
    /// and it genuinely does not: the old flat list needed this bound to stop a single response from
    /// spanning many days at once, while one day's own slots are already close to this size for an
    /// ordinary business day. What this bound protects against now is a fine slot granularity within
    /// one day (a five-minute grid over a twelve-hour day is 144 rows) rather than the day-spanning
    /// problem the original constant existed for - a different failure this factory's own caller
    /// still has to guard against, which is why the bound survives even though its target moved.
    /// </summary>
    private const int TimeSlotPageSize = 10;

    public static ModuleStep ServiceChoice(IReadOnlyList<BookableServiceRow> services, string locale)
    {
        var strings = Strings.For(locale);
        return ModuleStep.ChoiceListStep(
            strings.WhichService,
            [.. services.Select(s => new ModuleAction(DescribeService(s, strings), s.ServiceId.Value.ToString()))]);
    }

    /// <summary>Unconditional - offered whether the calendar has one worker or several. `25-33`'s own
    /// instruction was to make this step's existing placement in the flow deliberate and visible, not
    /// to build worker choice from scratch or to fold it into the date/time picker: it stays exactly
    /// where it already was, immediately <i>before</i> the date round below, which is the answer to
    /// "before, after, or folded into" the redesigned picker.</summary>
    public static ModuleStep WorkerChoice(IReadOnlyList<BookableWorkerRow> workers, string locale) =>
        ModuleStep.ChoiceListStep(
            Strings.For(locale).WhichWorker,
            [.. workers.Select(w => new ModuleAction(w.DisplayName, w.WorkerId.Value.ToString()))]);

    /// <summary>`25-33`: the picker's first round - a calendar of upcoming dates that actually have
    /// availability, not a flat list of every slot across every day. <paramref name="slots"/> is
    /// expected pre-sorted by <see cref="OpenSlotRow.StartsAt"/> ascending (the read store's own
    /// query order, <c>ReplyToModuleTaskHandler</c>'s own remarks) - <c>GroupBy</c> preserves the
    /// order keys are first seen in, so grouping by <see cref="OpenSlotRow.LocalDate"/> yields dates
    /// in calendar order for free, with no separate sort step. The action's own <c>value</c> is the
    /// ISO date string (<see cref="FormatDateValue"/>), echoed back on the reply that answers this
    /// step and parsed by <c>ReplyToModuleTaskHandler.HandleDateChosenAsync</c>.</summary>
    public static ModuleStep DateChoice(IReadOnlyList<OpenSlotRow> slots, string locale)
    {
        var strings = Strings.For(locale);
        var dates = slots
            .GroupBy(s => s.LocalDate)
            .Take(DatePageSize)
            .Select(g => (Date: g.Key, FirstStartsAt: g.First().StartsAt))
            .ToList();

        return ModuleStep.DateTimePickerStep(
            strings.PickADate,
            [.. dates.Select(d => new SlotOption(FormatDateValue(d.Date), d.FirstStartsAt, strings.FormatDate(d.Date)))],
            // `25-154`: the visible button label only - the author's own live-testing ask was that a
            // full weekday name is too long for a button, not that the date round's other renderings
            // (the `SlotOption.Label` line above, still unused by `ago-widget` today per
            // `render.ts`'s own comment, and every other `FormatDate` caller in this file) should
            // shorten too. See <see cref="Strings.FormatDateShort"/> for why this is a new method
            // beside <see cref="Strings.FormatDate"/> rather than a parameter on it.
            [.. dates.Select(d => new ModuleAction(strings.FormatDateShort(d.Date), FormatDateValue(d.Date)))]);
    }

    /// <summary>`25-33`: the picker's second round - the times available on the one date the visitor
    /// already chose. <paramref name="slotsForDate"/> is expected already filtered to
    /// <paramref name="date"/> by the caller (<c>ReplyToModuleTaskHandler</c>'s own
    /// <c>GetSlotsForDateAsync</c>) - this factory only truncates and renders, the same split
    /// <see cref="DateChoice"/> draws between "which rows" (the caller's job) and "how many fit in a
    /// step, and how they read" (this factory's). Each slot's own label is time-only
    /// (<see cref="DescribeTimeOnly"/>), not the full date-and-time <see cref="DescribeRange"/> the
    /// pre-`25-33` flat list used - the prompt itself already names the date, and repeating it on
    /// every one of up to <see cref="TimeSlotPageSize"/> lines is exactly the redundant wall of text
    /// this redesign exists to remove.
    ///
    /// <para><b>`25-145`: <paramref name="zone"/>/<paramref name="wallClock"/> convert every label into
    /// the calendar's own local time</b> - see <see cref="DescribeTimeOnly"/>'s own remarks. The
    /// prompt's own date name (<paramref name="date"/>, via <see cref="Strings.FormatDate"/>) needs no
    /// such conversion: it is <see cref="OpenSlotRow.LocalDate"/>, already computed correctly in the
    /// calendar's own zone at materialisation time (adr/0049) - converting it a second time here would
    /// be double conversion, not a fix.</para>
    /// </summary>
    public static ModuleStep SlotChoice(
        IReadOnlyList<OpenSlotRow> slotsForDate, DateOnly date, string locale, CalendarTimeZone zone,
        IWallClockResolver wallClock)
    {
        var strings = Strings.For(locale);
        var prompt = string.Format(CultureInfo.InvariantCulture, strings.PickATimeOnDate, strings.FormatDate(date));
        var page = slotsForDate.Take(TimeSlotPageSize).ToList();

        return ModuleStep.DateTimePickerStep(
            prompt,
            [.. page.Select(s => new SlotOption(s.EventId.Value.ToString(), s.StartsAt, DescribeTimeOnly(s, zone, wallClock)))],
            [.. page.Select(s => new ModuleAction(DescribeTimeOnly(s, zone, wallClock), s.EventId.Value.ToString()))]);
    }

    /// <summary>`20-09`: emits <see cref="ModuleStepKind.VerifiedPhoneForm"/> when
    /// <paramref name="requiresVerifiedPhone"/> is <see langword="true"/> - the signal to Chat that the
    /// next reply must carry proof of control over the number, not merely the number itself
    /// (`docs/adr/0082-*`). The wire payload shape is unchanged either way
    /// (<c>ChatModuleTaskEndpoints.ToStepDto</c>'s own remarks).
    ///
    /// <para><b>`25-39`: <paramref name="requiresVerifiedPhone"/> is <see langword="false"/> only for
    /// the fallback case a tenant's own `AcceptUnverifiedPhone` setting names explicitly - a
    /// visitor reaching this step with no phone already known.</b> Emits plain
    /// <see cref="ModuleStepKind.Form"/> instead: Chat's own gate
    /// (<c>RouteConversationToModuleHandler.ContinueActiveTaskAsync</c>) only demands `14-15` evidence
    /// ahead of a <c>verified_phone_form</c> reply, so a plain <c>form</c> here is what actually turns
    /// the requirement off on the wire, not merely a hint this handler could ignore.</para>
    ///
    /// <para><b>`25-38`: <paramref name="knownPhone"/> reshapes the prompt, never the field itself.</b>
    /// There is no wire-level "default value" for a form field in this contract, and adding one would
    /// only ever be honoured by a renderer nobody in this change's own scope can update (`ago-widget`'s
    /// own input rendering) - a channel-agnostic answer that already reaches every renderer this
    /// contract has today (the widget's own form, and every text-only channel's fallback,
    /// `PrimitiveTextRenderer`) is to name the number in the prompt itself and ask the visitor to
    /// confirm or replace it, which is exactly what "sees it prefilled, not re-typed from scratch"
    /// needs in spirit without inventing plumbing nothing yet renders.</para>
    /// </summary>
    public static ModuleStep PhoneForm(string locale, bool requiresVerifiedPhone, string? knownPhone)
    {
        var strings = Strings.For(locale);
        var prompt = knownPhone is { } phone
            ? string.Format(CultureInfo.InvariantCulture, strings.PhoneFormPromptWithKnownNumber, phone)
            : strings.PhoneFormPrompt;

        return requiresVerifiedPhone
            ? ModuleStep.VerifiedPhoneFormStep(prompt, "phone", strings.PhoneFieldLabel)
            : ModuleStep.FormStep(prompt, "phone", strings.PhoneFieldLabel);
    }

    /// <summary>`25-145`: <paramref name="zone"/>/<paramref name="wallClock"/> convert the booking's own
    /// instants into the calendar's local time before <see cref="DescribeRange"/> ever formats them -
    /// see that method's own remarks for the shape.</summary>
    public static ModuleStep Confirmation(
        string serviceName, string workerName, DateTimeOffset startsAt, DateTimeOffset endsAt, string locale,
        CalendarTimeZone zone, IWallClockResolver wallClock)
    {
        var strings = Strings.For(locale);
        return ModuleStep.ConfirmationStep(
            strings.Booked,
            [
                new ConfirmationLine(strings.ServiceLabel, serviceName),
                new ConfirmationLine(strings.WithLabel, workerName),
                new ConfirmationLine(strings.WhenLabel, DescribeRange(startsAt, endsAt, zone, wallClock, strings)),
            ]);
    }

    /// <summary>
    /// `23-35`: the price reaches this channel too, not only the widget - <see cref="BookableServiceRow"/>
    /// is the one read both consumers share, and a service priced on the widget but silent on a text
    /// channel would be the exact "one channel shows it, another doesn't" inconsistency `20-06`'s own
    /// "expressible as a prompt and a list of labelled choices" constraint exists to rule out.
    /// </summary>
    private static string DescribeService(BookableServiceRow service, Strings strings)
    {
        var duration = $"{service.DurationMinutes}{strings.MinutesSuffix}";
        if (service.PriceMinorUnits is not { } minorUnits)
        {
            return $"{service.Name} ({duration})";
        }

        return $"{service.Name} ({duration}, {DescribePrice(minorUnits, service.PriceIsFrom, strings)})";
    }

    /// <summary>v1's only currency is <see cref="Ago.Calendar.Domain.Money.RubleCode"/> - see its own
    /// remarks for why this is not yet a lookup table keyed by a currency code nobody has used.
    /// `25-37`: the surrounding word ("from"/"от") localises; the currency code itself does not - the
    /// backlog item's own instruction, since <c>RUB</c> is not a word to translate.</summary>
    private static string DescribePrice(int minorUnits, bool isFrom, Strings strings)
    {
        var amount = minorUnits % 100 == 0
            ? (minorUnits / 100).ToString(CultureInfo.InvariantCulture)
            : (minorUnits / 100m).ToString("0.00", CultureInfo.InvariantCulture);

        return isFrom ? $"{strings.PriceFromPrefix}{amount} RUB" : $"{amount} RUB";
    }

    /// <summary>
    /// `25-145`: the calendar's own zone, never UTC - date-and-time.md rule 1 says no visitor IANA
    /// zone is knowable in a chat channel, but that rule is about *guessing the visitor's own*; the
    /// zone this method renders in is a tenant-configured fact about the calendar
    /// (<see cref="BookingCalendar.TimeZone"/>), not a guess, and rendering it as UTC - the pre-`25-145`
    /// behaviour - was the actual live defect this item fixes, not a safe fallback for one. Full date
    /// (<see cref="Strings.FormatDate"/>, itself grown a year and stopped abbreviating by this same
    /// item) plus a start-end time range plus the zone's own abbreviation
    /// (<see cref="ZoneAbbreviation"/>) - unlike <see cref="DescribeTimeOnly"/>'s time-only sibling,
    /// this one names the date because a confirmation card is read on its own, with no surrounding
    /// prompt that already said which day it is.
    ///
    /// <para>Still one of the two strings in this file <see cref="Strings"/> does not own (the other
    /// is <see cref="DescribeTimeOnly"/>): the zone abbreviation table is genuinely zone data, not
    /// language data - it renders identically whichever <paramref name="locale"/> chose <paramref
    /// name="strings"/>, the same "an international technical abbreviation, not a translated word"
    /// reasoning <c>ago-console</c>'s own <c>time/format.ts</c> already applies to its zone
    /// labels.</para>
    /// </summary>
    private static string DescribeRange(
        DateTimeOffset startsAt, DateTimeOffset endsAt, CalendarTimeZone zone, IWallClockResolver wallClock,
        Strings strings)
    {
        var localStart = wallClock.ToLocal(zone, startsAt);
        var localEnd = wallClock.ToLocal(zone, endsAt);
        var abbreviation = ZoneAbbreviation(zone, localStart.Offset);
        var date = strings.FormatDate(DateOnly.FromDateTime(localStart.DateTime));

        return $"{date}, {localStart:HH:mm} - {localEnd:HH:mm} {abbreviation}";
    }

    /// <summary>`25-33`: the time round's own label - time only, no date, because
    /// <see cref="SlotChoice"/>'s own prompt already names the date once for the whole step. `25-145`:
    /// the calendar's own zone, converted through <paramref name="wallClock"/> exactly like
    /// <see cref="DescribeRange"/> - the two are the "confirmation card's own new format" pairing that
    /// item's own Scope names explicitly, and both share <see cref="ZoneAbbreviation"/> so the two
    /// surfaces can never render the same instant under two different zone labels.</summary>
    private static string DescribeTimeOnly(OpenSlotRow slot, CalendarTimeZone zone, IWallClockResolver wallClock)
    {
        var localStart = wallClock.ToLocal(zone, slot.StartsAt);
        var localEnd = wallClock.ToLocal(zone, slot.EndsAt);
        var abbreviation = ZoneAbbreviation(zone, localStart.Offset);

        return $"{localStart:HH:mm} - {localEnd:HH:mm} {abbreviation}";
    }

    /// <summary>
    /// `25-145`: Russia's own regional time-zone abbreviation scheme, hand-written per zone rather than
    /// derived from `TimeZoneInfo`'s own display name or any ICU table - the identical "closed set,
    /// hand-written, no ICU/CultureInfo dependency" posture <see cref="Strings"/>'s weekday/month
    /// tables already take, and for the same already-burned reason
    /// (<see cref="SystemWallClockResolver"/>'s own remarks on `25-26`'s missing-tzdata incident): this
    /// table trusts nothing about what the host's OS happens to have installed. Keyed by IANA id, the
    /// identical eleven <c>ago-console</c>'s own <c>calendarFormat.tsx</c> timezone picker (`25-16`)
    /// offers and <c>time/format.ts</c>'s own <c>RUSSIAN_ZONE_ABBREVIATIONS</c> already renders with -
    /// the same table, independently re-typed here rather than shared, because the two live in
    /// different repositories with no shared package between them (`docs/architecture/repositories.md`)
    /// and the values are frozen government-assigned abbreviations, not something a translator edits.
    /// A zone missing from this table (every one of the eleven is fixed-offset, no DST, since Russia's
    /// 2014 return to permanent standard time - `24-17`) is deliberately not any zone this table could
    /// misrender: it falls through to the bare offset instead of guessing a made-up abbreviation.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> RussianZoneAbbreviations =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Europe/Kaliningrad"] = "МСК-1",
            ["Europe/Moscow"] = "МСК",
            ["Europe/Samara"] = "МСК+1",
            ["Asia/Yekaterinburg"] = "МСК+2",
            ["Asia/Omsk"] = "МСК+3",
            ["Asia/Krasnoyarsk"] = "МСК+4",
            ["Asia/Irkutsk"] = "МСК+5",
            ["Asia/Yakutsk"] = "МСК+6",
            ["Asia/Vladivostok"] = "МСК+7",
            ["Asia/Magadan"] = "МСК+8",
            ["Asia/Kamchatka"] = "МСК+9",
        };

    /// <summary>The label a rendered instant's own zone gets: the curated Russian abbreviation above
    /// when <paramref name="zone"/> is one of the eleven, or else a bare, unambiguous UTC-offset string
    /// (<c>"+05:00"</c>) built from <paramref name="localOffset"/> itself - never a guessed
    /// abbreviation for a zone this table does not name, and never the literal word "UTC" for a
    /// calendar's own configured zone (that was the defect this item fixes).</summary>
    private static string ZoneAbbreviation(CalendarTimeZone zone, TimeSpan localOffset)
    {
        if (RussianZoneAbbreviations.TryGetValue(zone.Value, out var abbreviation))
        {
            return abbreviation;
        }

        var magnitude = localOffset.Duration();
        var sign = localOffset < TimeSpan.Zero ? '-' : '+';
        return $"{sign}{magnitude:hh\\:mm}";
    }

    /// <summary>`25-33`: the wire value a date round's own action carries - ISO 8601
    /// (<c>"yyyy-MM-dd"</c>), culture-invariant and unambiguous, the same format
    /// <see cref="Domain.ChatBookingTask.ChooseDate"/> re-derives to set
    /// <see cref="Domain.ChatBookingTask.LastAppliedValue"/> and <c>ReplyToModuleTaskHandler</c>
    /// parses the reply back out of. Not locale-dependent - a wire value, not display text, the same
    /// split every other id-shaped <c>ModuleAction.Value</c> in this file already draws.</summary>
    private static string FormatDateValue(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>`25-37`: the whole closed vocabulary this factory renders, in one language - a plain
    /// record rather than a resource file or a third-party i18n library: two members, the same
    /// "closed set of two, hand-written" shape <c>ago-console</c>'s own <c>LOCALE_LABELS</c> already
    /// uses for the identical language pair, and pulling in a translation framework for two fixed
    /// tables would be the premature generalisation `clean-architecture.md` warns against.</summary>
    private sealed record Strings(
        string WhichService,
        string WhichWorker,
        string PickADate,
        string PickATimeOnDate,
        string PhoneFormPrompt,
        string PhoneFormPromptWithKnownNumber,
        string PhoneFieldLabel,
        string Booked,
        string ServiceLabel,
        string WithLabel,
        string WhenLabel,
        string PriceFromPrefix,
        string MinutesSuffix,
        IReadOnlyList<string> Weekdays,
        IReadOnlyList<string> ShortWeekdays,
        IReadOnlyList<string> Months,
        bool MonthBeforeDay)
    {
        private static readonly Strings English = new(
            WhichService: "What would you like to book?",
            WhichWorker: "Who would you like to book with?",
            // `25-33`: two rounds replace the old flat "Pick a time:" - a date round, then a time
            // round for the chosen date, each with its own prompt.
            PickADate: "Pick a date:",
            PickATimeOnDate: "Pick a time on {0}:",
            PhoneFormPrompt: "What's the best phone number to reach you on?",
            // `25-38`: names why this is being asked again, distinct from the contact info already on
            // file - the gap this backlog item's own subject names ("no explanation").
            PhoneFormPromptWithKnownNumber:
                "To confirm this booking, is {0} still the best number to reach you on? This confirms " +
                "the number for the booking itself - separate from any contact details already on " +
                "file. Reply with it again, or send a different number.",
            PhoneFieldLabel: "Phone number",
            Booked: "You're booked!",
            ServiceLabel: "Service",
            WithLabel: "With",
            WhenLabel: "When",
            PriceFromPrefix: "from ",
            MinutesSuffix: " min",
            // `25-33`/`25-145`: hand-written, not CultureInfo/ICU-derived - the identical "no
            // third-party i18n, two fixed tables" posture this whole record already takes, and it
            // sidesteps a real, already-burned risk in this exact codebase: the deployed base image's
            // ICU data was only confirmed present after `25-26` found the *plain* chiseled tag shipped
            // no tzdata at all (this Dockerfile's own comment) - trusting a *second* OS-provided data
            // set (culture name tables) for locale text this factory already owns everywhere else would
            // be the identical risk shape, not a new one. `25-145`: full names, not the pre-`25-145`
            // three-letter abbreviations - the author's own reasoning, stated in the backlog item, is
            // that a full weekday name orients a client better than a bare day number.
            Weekdays: ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"],
            // `25-154`: the exact three-letter forms this table itself carried before `25-145` grew
            // `Weekdays` to full names - kept here for the one caller (the date-choice button) that
            // still wants a short weekday, now that `Weekdays` itself means "full name" everywhere
            // else in this file.
            ShortWeekdays: ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"],
            Months:
            [
                "January", "February", "March", "April", "May", "June", "July", "August", "September",
                "October", "November", "December",
            ],
            MonthBeforeDay: true);

        private static readonly Strings Russian = new(
            WhichService: "Выберите услугу для записи:",
            WhichWorker: "К кому вы хотите записаться?",
            PickADate: "Выберите дату:",
            PickATimeOnDate: "Выберите время на {0}:",
            PhoneFormPrompt: "Какой номер телефона лучше всего подходит, чтобы с вами связаться?",
            PhoneFormPromptWithKnownNumber:
                "Чтобы подтвердить бронирование: {0} - это тот номер, по которому лучше всего с вами " +
                "связаться? Это подтверждение номера именно для бронирования - отдельно от контактных " +
                "данных, которые уже есть в системе. Напишите его ещё раз или укажите другой номер.",
            PhoneFieldLabel: "Номер телефона",
            Booked: "✅ Готово!",
            ServiceLabel: "Услуга",
            WithLabel: "С кем",
            WhenLabel: "Когда",
            PriceFromPrefix: "от ",
            MinutesSuffix: " мин",
            // `25-145`: full names, and the month table is genitive case ("сентября", not the
            // dictionary-form "сентябрь") - Russian grammar for "18 <month>" demands the genitive, the
            // same way English needs no case distinction here at all. Getting this wrong is not a
            // rounding error the way a wrong abbreviation would be; it reads as broken Russian.
            Weekdays: ["воскресенье", "понедельник", "вторник", "среда", "четверг", "пятница", "суббота"],
            // `25-154`: the pre-`25-145` two-letter forms, capitalized - the old table
            // (`git show 75221cd~1`) read lowercase ("вт") because it was the *only* form this record
            // had; now that it sits beside the full `Weekdays` name, a button's own leading word reads
            // capitalized, matching how the English `ShortWeekdays` entries were already cased.
            ShortWeekdays: ["Вс", "Пн", "Вт", "Ср", "Чт", "Пт", "Сб"],
            Months:
            [
                "января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября",
                "октября", "ноября", "декабря",
            ],
            MonthBeforeDay: false);

        /// <summary>`25-33`/`25-145`: this record's own hand-written weekday/month names, never
        /// <c>CultureInfo</c>/ICU - see <see cref="Weekdays"/>'s own remarks on why. `25-145` grew both
        /// tables from three-letter abbreviations to full names and added the year: English reads
        /// "Friday, September 18, 2026", Russian "пятница, 18 сентября 2026" - each language's own
        /// natural weekday/day/month order and punctuation (English's comma before the year; Russian's
        /// lack of one), not one format forced onto both.</summary>
        public string FormatDate(DateOnly date)
        {
            var weekday = Weekdays[(int)date.DayOfWeek];
            var month = Months[date.Month - 1];
            return MonthBeforeDay
                ? $"{weekday}, {month} {date.Day}, {date.Year}"
                : $"{weekday}, {date.Day} {month} {date.Year}";
        }

        /// <summary>`25-154`: the date-choice button's own label - <see cref="ShortWeekdays"/> instead
        /// of <see cref="Weekdays"/>, day/month/year exactly as <see cref="FormatDate"/> renders them.
        /// A new method beside <see cref="FormatDate"/>, deliberately, rather than an
        /// <c>abbreviateWeekday</c> parameter on it: <see cref="FormatDate"/> already has one caller
        /// per round (<see cref="DescribeRange"/>, <see cref="DescribeTimeOnly"/>'s prompt via
        /// <see cref="SlotChoice"/>) that must keep reading the full weekday name, and a boolean
        /// parameter defaulting to <see langword="false"/> would still leave every one of those call
        /// sites able to silently start passing <see langword="true"/> later with nothing at the call
        /// site itself signalling "this is the one place that reads short". Naming the short form its
        /// own method makes that impossible by construction - <see cref="DateChoice"/> is the only
        /// caller, and it says so.</summary>
        public string FormatDateShort(DateOnly date)
        {
            var weekday = ShortWeekdays[(int)date.DayOfWeek];
            var month = Months[date.Month - 1];
            return MonthBeforeDay
                ? $"{weekday}, {month} {date.Day}, {date.Year}"
                : $"{weekday}, {date.Day} {month} {date.Year}";
        }

        /// <summary>Anything but a recognised <c>"Ru"</c> (case-insensitive, matching how loosely every
        /// other string comparison at this wire boundary already treats a hand-synchronized value)
        /// renders in English - a genuinely unconfigured value, a locale Chat itself defaults to
        /// (<c>Ago.Chat.Domain.Locale.En</c>'s own remarks), or a locale this module has no strings for
        /// yet, all read the same safe way rather than throwing.</summary>
        public static Strings For(string? locale) =>
            string.Equals(locale, "Ru", StringComparison.OrdinalIgnoreCase) ? Russian : English;
    }
}
