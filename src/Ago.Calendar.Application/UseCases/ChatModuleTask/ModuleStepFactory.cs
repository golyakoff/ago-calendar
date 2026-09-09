using System.Globalization;
using Ago.Calendar.Application.Abstractions;

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
            [.. dates.Select(d => new ModuleAction(strings.FormatDate(d.Date), FormatDateValue(d.Date)))]);
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
    /// this redesign exists to remove.</summary>
    public static ModuleStep SlotChoice(IReadOnlyList<OpenSlotRow> slotsForDate, DateOnly date, string locale)
    {
        var strings = Strings.For(locale);
        var prompt = string.Format(CultureInfo.InvariantCulture, strings.PickATimeOnDate, strings.FormatDate(date));
        var page = slotsForDate.Take(TimeSlotPageSize).ToList();

        return ModuleStep.DateTimePickerStep(
            prompt,
            [.. page.Select(s => new SlotOption(s.EventId.Value.ToString(), s.StartsAt, DescribeTimeOnly(s)))],
            [.. page.Select(s => new ModuleAction(DescribeTimeOnly(s), s.EventId.Value.ToString()))]);
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

    public static ModuleStep Confirmation(
        string serviceName, string workerName, DateTimeOffset startsAt, DateTimeOffset endsAt, string locale)
    {
        var strings = Strings.For(locale);
        return ModuleStep.ConfirmationStep(
            strings.Booked,
            [
                new ConfirmationLine(strings.ServiceLabel, serviceName),
                new ConfirmationLine(strings.WithLabel, workerName),
                new ConfirmationLine(strings.WhenLabel, DescribeRange(startsAt, endsAt)),
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

    /// <summary>UTC, labelled - date-and-time.md rule 1: no IANA zone is known for the visitor on the
    /// other end of a chat conversation, so this renders UTC rather than guessing one, exactly like a
    /// renderer with no zone information is instructed to elsewhere in this codebase. `25-37`: stays
    /// unlocalized on purpose - the backlog item's own instruction ("only the surrounding prose needs a
    /// language") - so this is one of two strings in this file <see cref="Strings"/> does not own (the
    /// other is <see cref="DescribeTimeOnly"/>, its own time-only sibling for the `25-33` time
    /// round).</summary>
    private static string DescribeRange(DateTimeOffset startsAt, DateTimeOffset endsAt) =>
        $"{startsAt:yyyy-MM-dd HH:mm} UTC - {endsAt:HH:mm} UTC";

    /// <summary>`25-33`: the time round's own label - time only, no date, because
    /// <see cref="SlotChoice"/>'s own prompt already names the date once for the whole step. Still
    /// UTC and still unlocalized, for the identical reason <see cref="DescribeRange"/> is.</summary>
    private static string DescribeTimeOnly(OpenSlotRow slot) => $"{slot.StartsAt:HH:mm} - {slot.EndsAt:HH:mm} UTC";

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
            // `25-33`: hand-written, not CultureInfo/ICU-derived - the identical "no third-party i18n,
            // two fixed tables" posture this whole record already takes, and it sidesteps a real,
            // already-burned risk in this exact codebase: the deployed base image's ICU data was only
            // confirmed present after `25-26` found the *plain* chiseled tag shipped no tzdata at all
            // (this Dockerfile's own comment) - trusting a *second* OS-provided data set (culture
            // name tables) for locale text this factory already owns everywhere else would be the
            // identical risk shape, not a new one.
            Weekdays: ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"],
            Months:
            [
                "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
            ],
            MonthBeforeDay: true);

        private static readonly Strings Russian = new(
            WhichService: "Что вы хотите забронировать?",
            WhichWorker: "С кем вы хотите записаться?",
            PickADate: "Выберите дату:",
            PickATimeOnDate: "Выберите время на {0}:",
            PhoneFormPrompt: "Какой номер телефона лучше всего подходит, чтобы с вами связаться?",
            PhoneFormPromptWithKnownNumber:
                "Чтобы подтвердить бронирование: {0} - это тот номер, по которому лучше всего с вами " +
                "связаться? Это подтверждение номера именно для бронирования - отдельно от контактных " +
                "данных, которые уже есть в системе. Напишите его ещё раз или укажите другой номер.",
            PhoneFieldLabel: "Номер телефона",
            Booked: "Вы записаны!",
            ServiceLabel: "Услуга",
            WithLabel: "С кем",
            WhenLabel: "Когда",
            PriceFromPrefix: "от ",
            MinutesSuffix: " мин",
            Weekdays: ["вс", "пн", "вт", "ср", "чт", "пт", "сб"],
            Months:
            [
                "янв", "фев", "мар", "апр", "мая", "июн", "июл", "авг", "сен", "окт", "ноя", "дек",
            ],
            MonthBeforeDay: false);

        /// <summary>`25-33`: this record's own hand-written weekday/month names, never
        /// <c>CultureInfo</c>/ICU - see <see cref="Weekdays"/>'s own remarks on why. English reads
        /// "Tue, Sep 15"; Russian reads "вт, 15 сен" - each language's own natural day/month order,
        /// not one format forced onto both.</summary>
        public string FormatDate(DateOnly date)
        {
            var weekday = Weekdays[(int)date.DayOfWeek];
            var month = Months[date.Month - 1];
            return MonthBeforeDay ? $"{weekday}, {month} {date.Day}" : $"{weekday}, {date.Day} {month}";
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
