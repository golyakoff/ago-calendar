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
    public static ModuleStep ServiceChoice(IReadOnlyList<BookableServiceRow> services, string locale)
    {
        var strings = Strings.For(locale);
        return ModuleStep.ChoiceListStep(
            strings.WhichService,
            [.. services.Select(s => new ModuleAction(DescribeService(s, strings), s.ServiceId.Value.ToString()))]);
    }

    public static ModuleStep WorkerChoice(IReadOnlyList<BookableWorkerRow> workers, string locale) =>
        ModuleStep.ChoiceListStep(
            Strings.For(locale).WhichWorker,
            [.. workers.Select(w => new ModuleAction(w.DisplayName, w.WorkerId.Value.ToString()))]);

    public static ModuleStep SlotChoice(IReadOnlyList<OpenSlotRow> slots, string locale) =>
        ModuleStep.DateTimePickerStep(
            Strings.For(locale).PickATime,
            [.. slots.Select(s => new SlotOption(s.EventId.Value.ToString(), s.StartsAt, DescribeSlot(s)))],
            [.. slots.Select(s => new ModuleAction(DescribeSlot(s), s.EventId.Value.ToString()))]);

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

    private static string DescribeSlot(OpenSlotRow slot) => DescribeRange(slot.StartsAt, slot.EndsAt);

    /// <summary>UTC, labelled - date-and-time.md rule 1: no IANA zone is known for the visitor on the
    /// other end of a chat conversation, so this renders UTC rather than guessing one, exactly like a
    /// renderer with no zone information is instructed to elsewhere in this codebase. `25-37`: stays
    /// unlocalized on purpose - the backlog item's own instruction ("only the surrounding prose needs a
    /// language") - so this is the one string in this file <see cref="Strings"/> does not own.</summary>
    private static string DescribeRange(DateTimeOffset startsAt, DateTimeOffset endsAt) =>
        $"{startsAt:yyyy-MM-dd HH:mm} UTC - {endsAt:HH:mm} UTC";

    /// <summary>`25-37`: the whole closed vocabulary this factory renders, in one language - a plain
    /// record rather than a resource file or a third-party i18n library: two members, the same
    /// "closed set of two, hand-written" shape <c>ago-console</c>'s own <c>LOCALE_LABELS</c> already
    /// uses for the identical language pair, and pulling in a translation framework for two fixed
    /// tables would be the premature generalisation `clean-architecture.md` warns against.</summary>
    private sealed record Strings(
        string WhichService,
        string WhichWorker,
        string PickATime,
        string PhoneFormPrompt,
        string PhoneFormPromptWithKnownNumber,
        string PhoneFieldLabel,
        string Booked,
        string ServiceLabel,
        string WithLabel,
        string WhenLabel,
        string PriceFromPrefix,
        string MinutesSuffix)
    {
        private static readonly Strings English = new(
            WhichService: "What would you like to book?",
            WhichWorker: "Who would you like to book with?",
            PickATime: "Pick a time:",
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
            MinutesSuffix: " min");

        private static readonly Strings Russian = new(
            WhichService: "Что вы хотите забронировать?",
            WhichWorker: "С кем вы хотите записаться?",
            PickATime: "Выберите время:",
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
            MinutesSuffix: " мин");

        /// <summary>Anything but a recognised <c>"Ru"</c> (case-insensitive, matching how loosely every
        /// other string comparison at this wire boundary already treats a hand-synchronized value)
        /// renders in English - a genuinely unconfigured value, a locale Chat itself defaults to
        /// (<c>Ago.Chat.Domain.Locale.En</c>'s own remarks), or a locale this module has no strings for
        /// yet, all read the same safe way rather than throwing.</summary>
        public static Strings For(string? locale) =>
            string.Equals(locale, "Ru", StringComparison.OrdinalIgnoreCase) ? Russian : English;
    }
}
