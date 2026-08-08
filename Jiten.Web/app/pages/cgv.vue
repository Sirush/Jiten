<script setup lang="ts">
  const { legal, configured } = useLegalIdentity();
  const doc = LEGAL_DOCUMENTS.cgv;
  const prices = JITEN_PLUS_PRICES;

  useSeoMeta({ title: 'Terms of Sale (CGV) - Jiten+' });
  useRobotsRule({ noindex: true, follow: true });
</script>

<template>
  <div class="legal-doc mx-auto max-w-3xl px-6 py-10 leading-relaxed text-gray-800 dark:text-gray-200">
    <h1 class="mb-2 text-center text-3xl font-bold text-gray-900 dark:text-gray-100">Terms of Sale / Conditions Générales de Vente (Jiten+)</h1>
    <p class="mb-1 text-center text-gray-600 dark:text-gray-400">
      <b>Seller:</b> Jitensoft, entreprise individuelle (see the <NuxtLink to="/mentions-legales">Legal notice</NuxtLink>)
    </p>
    <p class="mb-1 text-center text-gray-600 dark:text-gray-400"><b>Version:</b> {{ doc.version }} — <b>Effective:</b> {{ doc.effectiveDate }}</p>
    <p class="mb-8 text-center italic text-gray-500 dark:text-gray-400">
      <NuxtLink to="/cgv-fr">Version française disponible ici</NuxtLink>. For consumers resident in France, the French version prevails.
    </p>

    <div
      v-if="!configured"
      class="mb-10 rounded-lg border border-amber-400 bg-amber-50 p-4 font-semibold text-amber-800 dark:border-amber-600 dark:bg-amber-950 dark:text-amber-200"
    >
      Legal identity configuration is missing.
    </div>

    <p>
      These Terms of Sale (the "CGV") govern the purchase of Jiten+, the optional paid tier of the Jiten service. They supplement the
      <NuxtLink to="/terms">Terms of Use</NuxtLink> ("CGU"), which continue to apply. Where the two conflict on a matter of sale, these CGV prevail.
    </p>
    <p>
      The version of the CGV accepted at the time of your order governs your purchase. Subsequent versions apply to renewals only as provided in article 12.
    </p>

    <section class="mb-10">
      <h2 class="section-title">1. Seller</h2>
      <p>
        {{ legal.publisherName }}, trading as Jitensoft, entreprise individuelle registered in France under SIREN {{ legal.siren }} (SIRET
        {{ legal.siret }}), registered address {{ legal.address }}, contact <a href="mailto:contact@jiten.moe">contact@jiten.moe</a>.
      </p>
      <p>Jitensoft is the seller of record. Payment is processed by Stripe on Jitensoft's behalf; Stripe is not the seller.</p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">2. Scope, and who may buy</h2>
      <p>
        <b>2.1</b> These CGV apply to consumers and to non-consumer purchasers. Provisions expressed to apply to consumers apply only where the purchaser is a
        natural person acting for purposes outside their trade, business, craft or profession, within the meaning of the preliminary article of the French
        Consumer Code.
      </p>
      <p>
        <b>2.2</b> You must hold a Jiten Account and be at least 15 years old, or have the consent of a holder of parental responsibility. A minor must have
        that consent before purchasing.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">3. What Jiten+ is</h2>
      <p>
        <b>3.1</b> Jiten+ is a subscription to additional personal features layered on the free Jiten service. The current feature set, tiers and applicable
        limits are described on the <NuxtLink to="/jiten-plus">Jiten+ page</NuxtLink>, which forms part of these CGV by reference.
      </p>
      <p>
        <b>3.2 What Jiten+ does not include.</b> Jiten+ confers no additional access to third-party textual material. Example sentences and all other
        reproductions of third-party expression remain available without payment, as undertaken at article 7.4 of the CGU. Jiten+ is sold on the basis of
        computation and storage performed for you, over your own data.
      </p>
      <p>
        <b>3.3 The free service is unaffected.</b> Purchasing Jiten+ does not remove or degrade any capability available without payment. The undertaking at
        article 4.2 of the CGU applies in full.
      </p>
      <p>
        <b>3.4</b> Jiten+ features may be added, improved or replaced during your subscription. Where we materially reduce the features of your subscribed
        tier, article 12.3 applies.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">4. Plans and prices</h2>
      <p><b>4.1</b> Jiten+ is offered as:</p>
      <table>
        <thead>
          <tr>
            <th>Plan</th>
            <th>Price</th>
            <th>Billing</th>
          </tr>
        </thead>
        <tbody>
          <tr>
            <td>Monthly</td>
            <td>{{ prices.monthlyEur }} EUR</td>
            <td>Recurring, monthly, until cancelled</td>
          </tr>
          <tr>
            <td>Yearly</td>
            <td>{{ prices.yearlyEur }} EUR</td>
            <td>Recurring, annually, until cancelled</td>
          </tr>
          <tr>
            <td>Lifetime</td>
            <td>{{ prices.lifetimeEur }} EUR</td>
            <td>One-off payment, when offered (see article 8)</td>
          </tr>
        </tbody>
      </table>
      <p><b>4.2</b> Prices displayed on the Jiten+ page at the time of your order prevail. Prices are in euros.</p>
      <p>
        <b>4.3 The price shown is the total you pay.</b> The amount displayed before you confirm payment is the total amount payable for the plan, inclusive
        of any tax applicable to your purchase. No tax, fee or surcharge is added at checkout or subsequently. If Jitensoft becomes liable to account for
        value added tax on your purchase, that liability is borne by Jitensoft out of the price stated under this article and does not increase the amount
        you pay. Any change to the price itself is governed by article 12.
      </p>
      <p>
        <b>4.4 Current tax status.</b> Jitensoft currently benefits from the French value added tax exemption for small businesses: <i>TVA non applicable,
        article 293 B du CGI</i>. No VAT is therefore charged on, or recoverable from, these prices, and the same mention appears on every invoice issued.
        This article states a fact about Jitensoft's current tax position; it is not a term of the contract, and a change to it does not alter the price
        payable under article 4.3.
      </p>
      <p><b>4.5</b> Any bank, currency conversion or card issuer charge is borne by you.</p>
      <p>
        <b>4.6</b> The Lifetime plan is offered only during announced periods. It may be withdrawn at any time. No right to purchase it arises from a past
        offering.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">5. Ordering and formation of the contract</h2>
      <p>
        <b>5.1</b> The ordering process is: select a plan on the Jiten+ page, review the plan, price and billing frequency, accept these CGV, give the
        declaration required by article 9 where applicable, and complete payment on the secure page hosted by Stripe.
      </p>
      <p>
        <b>5.2</b> Before payment you are shown the plan, the total amount, the billing frequency and the renewal behaviour. You are able to identify and
        correct errors before confirming.
      </p>
      <p><b>5.3</b> The contract is formed when payment is confirmed and we send confirmation by email.</p>
      <p>
        <b>5.4</b> We will send confirmation to the email address on your Account, on a durable medium, stating the plan, the amount paid, the billing
        frequency, the renewal date or access-through date, the declaration you gave under article 9, and a link to these CGV as accepted.
      </p>
      <p>
        <b>5.5</b> We may refuse an order where there is a reasonable suspicion of fraud, of abuse of promotional credits, or of breach of the CGU.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">6. Payment</h2>
      <p>
        <b>6.1</b> Payment is by the means offered at checkout, processed by Stripe Payments Europe, Ltd. Jitensoft does not receive or store your card
        details.
      </p>
      <p>
        <b>6.2</b> For recurring plans you authorise Jitensoft, through Stripe, to charge the payment method on file at each renewal, at the price then
        applicable under article 12.
      </p>
      <p>
        <b>6.3 Failed payment.</b> If a renewal payment fails, Stripe will retry over a limited period. If payment is not completed, Jiten+ access ends at
        the end of the paid period. Your data is retained in accordance with article 10. We will notify you before access ends.
      </p>
      <p><b>6.4</b> You are responsible for keeping your payment method valid and your billing email current.</p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">7. Duration, renewal and the renewal reminder</h2>
      <p>
        <b>7.1</b> Monthly and Yearly plans run for the stated period and renew automatically for successive periods of the same length until cancelled.
      </p>
      <p>
        <b>7.2 Renewal reminder.</b> In accordance with article L215-1 of the French Consumer Code, for any plan that renews automatically for a period of
        one year or more we will inform you in writing, by email, of the date by which you may object to renewal. That notice will be sent no earlier than
        three (3) months and no later than one (1) month before the end of the period during which you may object.
      </p>
      <p>
        If we fail to send that notice, you may terminate the contract free of charge at any time from the renewal date, and any sums paid after the renewal
        date will be refunded within thirty (30) days of termination, less any sums corresponding to the period actually used.
      </p>
      <p>
        <b>7.3</b> You may cancel at any time. Cancellation takes effect at the end of the current paid period. Jiten+ remains fully available until that
        date. Cancellation of a recurring plan does not give rise to a refund of the current period, save as provided in articles 7.2, 9 and 12.3.
      </p>
      <p>
        <b>7.4 How to cancel.</b> In accordance with article L215-1-1 of the French Consumer Code, cancellation is available online, by a means at least as
        simple as subscribing, from your <NuxtLink to="/settings/subscription">subscription settings</NuxtLink>. No telephone call, letter or contact with
        support is required.
      </p>
      <p>
        <b>7.5</b> After cancellation takes effect your Account reverts to the free tier. Article 10 governs what happens to data created using Jiten+
        features.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">8. Lifetime access</h2>
      <p>
        <b>8.1 What "lifetime" means.</b> Lifetime access means access for the operational lifetime of the Jiten service, not the lifetime of the purchaser.
        It is not perpetual in the sense of surviving the discontinuation of the Service.
      </p>
      <p>
        <b>8.2</b> Lifetime access is bound to the Account that purchased it. It is personal and non-transferable, and may not be sold, gifted or moved to
        another Account.
      </p>
      <p>
        <b>8.3</b> Lifetime access covers the Jiten+ tier as it evolves. It does not oblige Jitensoft to create any particular future feature, nor to include
        in Jiten+ any product that Jitensoft may later offer separately. Any such separate product will be genuinely additional and will not consist of a
        capability that formed part of Jiten+ at the time of purchase.
      </p>
      <p><b>8.4 If the Service ceases.</b> If Jitensoft permanently discontinues the Jiten service, we will:</p>
      <ul>
        <li>a) give at least ninety (90) days' public notice before access ends;</li>
        <li>b) keep data export available throughout that period, at no charge;</li>
        <li>c) refund holders of purchased Lifetime access on the following scale, calculated from the date of purchase to the date access ends:</li>
      </ul>
      <table>
        <thead>
          <tr>
            <th>Service ends within</th>
            <th>Refund of the price actually paid</th>
          </tr>
        </thead>
        <tbody>
          <tr>
            <td>0 to 12 months of purchase</td>
            <td>75%</td>
          </tr>
          <tr>
            <td>12 to 24 months of purchase</td>
            <td>50%</td>
          </tr>
          <tr>
            <td>24 to 36 months of purchase</td>
            <td>25%</td>
          </tr>
          <tr>
            <td>After 36 months</td>
            <td>none</td>
          </tr>
        </tbody>
      </table>
      <p>
        Paragraphs a) and b) apply to every Account, whether or not anything was paid. Paragraph c) is calculated on the price actually paid for the Lifetime
        plan, and confers no entitlement where access was granted free of charge under article 13.
      </p>
      <p>
        Holders of a recurring plan will not be charged for any period after the announced end date, and any sum already paid in respect of a period after
        that date will be refunded in full.
      </p>
      <p>
        This article does not apply where the Service ceases as a result of force majeure within the meaning of article 1218 of the French Civil Code, in
        which case Jitensoft will refund such sums as it is reasonably able to refund. It does not limit any right a consumer has that cannot lawfully be
        limited.
      </p>
      <p>
        <b>8.5 Transfer of the Service.</b> If Jitensoft transfers the Service to another operator, that operator must assume these CGV, including the
        undertakings at CGU articles 4.2 and 7.4 and at article 8.4 above, or the transfer will be treated as a discontinuation under article 8.4.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">9. Right of withdrawal</h2>
      <p><b>9.1</b> As a consumer you have a right of withdrawal of fourteen (14) days from the formation of the contract.</p>
      <p>
        <b>9.2 Waiver on immediate access.</b> Jiten+ is supplied immediately upon payment. Before completing payment you are asked to confirm, and must
        confirm, an express declaration that:
      </p>
      <ul>
        <li>a) you <b>request the supply of Jiten+ to begin immediately</b>, before the end of the fourteen-day period; and</li>
        <li>b) you <b>acknowledge that you thereby lose your right of withdrawal</b> once supply has begun.</li>
      </ul>
      <p>
        Where that declaration is given and supply has begun, the right of withdrawal is extinguished in accordance with article L221-28 13° of the French
        Consumer Code. The declaration is recorded and repeated in the confirmation sent under article 5.4.
      </p>
      <p>
        <b>9.3</b> If you do not give that declaration, the purchase cannot proceed at checkout. Contact
        <a href="mailto:contact@jiten.moe">contact@jiten.moe</a> to arrange a purchase with supply deferred until the end of the withdrawal period.
      </p>
      <p>
        <b>9.4 Voluntary refund policy.</b> Independently of article 9.2, and without prejudice to it, Jitensoft will refund in full any first purchase of
        Jiten+ cancelled within fourteen (14) days of payment, on request to <a href="mailto:contact@jiten.moe">contact@jiten.moe</a>, where the subscriber
        has not made substantial use of the paid features. This is a commercial commitment, not a restatement of a legal right, and does not apply to
        renewals or to repeat purchases by the same Account.
      </p>
      <p><b>9.5</b> Refunds are made by the original payment method within fourteen (14) days of our accepting the request.</p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">10. Your data if you stop paying</h2>
      <p><b>10.1</b> Ending a subscription does not delete your data.</p>
      <p><b>10.2</b> On lapse or cancellation:</p>
      <ul>
        <li>a) your vocabulary, study history, decks, statistics and Account remain intact and available on the free tier;</li>
        <li>
          b) files you uploaded using Jiten+ features, including card images and audio, are <b>retained, not deleted</b>. They remain viewable, downloadable
          and deletable by you. The ability to upload new files, and any feature that refreshes or regenerates premium output, stops;
        </li>
        <li>
          c) artefacts generated using Jiten+, such as saved frequency lists and immersion plans, remain viewable and exportable. Automatic updating stops;
        </li>
        <li>
          d) periodic allowances included in Jiten+, present or future, such as monthly feature credits or usage quotas, are per-period: they do not
          accumulate or carry over from one period to the next, and they cease when the subscription ends. Effects already produced by using an allowance
          are unaffected;
        </li>
        <li>e) export of your personal data remains available free of charge, as provided at CGU article 9.5.</li>
      </ul>
      <p>
        <b>10.3</b> We will not delete retained files as a consequence of non-payment. We may delete them at your request, on deletion of your Account, or
        where required by law. Where storage of retained files becomes economically unsustainable at scale, we will give affected Users at least ninety (90)
        days' notice and free export before any change, and will not apply such a change retroactively to files uploaded before the notice.
      </p>
      <p><b>10.4</b> Resubscribing restores full access to retained data without loss.</p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">11. Legal guarantee of conformity</h2>
      <p>
        <b>11.1</b> Jitensoft supplies Jiten+ in conformity with the contract and is liable for any lack of conformity existing at the time of supply and
        appearing during the period of supply, in accordance with articles L224-25-12 et seq. of the French Consumer Code.
      </p>
      <p><b>11.2</b> For a subscription supplied continuously, that liability runs for the whole period of supply.</p>
      <p>
        <b>11.3</b> Where a lack of conformity is established, you may require it to be brought into conformity. If that is impossible, disproportionate, or
        not done within a reasonable time or without significant inconvenience to you, you may obtain a price reduction or termination of the contract, on
        the terms set out in the Consumer Code.
      </p>
      <p>
        <b>11.4</b> These rights are in addition to the right of withdrawal at article 9 and to any commercial gesture we may offer. Nothing in these CGV
        limits them.
      </p>
      <p>
        <b>11.5</b> Jitensoft is also bound by the guarantee against hidden defects under articles 1641 et seq. of the French Civil Code where applicable.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">12. Price and plan changes</h2>
      <p><b>12.1</b> We may change the price of a plan for new purchases at any time.</p>
      <p>
        <b>12.2 Existing subscribers.</b> The price of a recurring subscription does not increase for as long as it remains continuously active: price
        increases apply only to new purchases, and to subscriptions taken out again after a cancellation. By exception, where a change results solely from
        a change in applicable taxes (for example VAT becoming chargeable on our sales), the tax-inclusive price may change accordingly. In that case we
        will give you written notice at least thirty (30) days before the renewal date on which it takes effect, stating the current price, the new price
        and its effective date, and reminding you how to cancel. If you do not wish to accept it, cancel before that renewal date; you will not be charged
        the new price.
      </p>
      <p>
        <b>12.3 Material reduction of features.</b> If we materially reduce the features of the tier you subscribe to, we will notify you at least thirty
        (30) days in advance. You may then terminate with effect from the date of the change and receive a refund of the unused portion of the current
        period, calculated pro rata. Holders of Lifetime access affected by a material reduction may claim under article 8.4 as though the Service had
        ceased on the date of the change, unless the reduction is remedied within ninety (90) days.
      </p>
      <p>
        <b>12.4</b> These CGV may be amended for future purchases and renewals. The version applicable to a renewal is the version in force thirty (30) days
        before the renewal date, and material amendments are notified under article 12.2.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">13. Promotional credits, trials and gratuitous grants</h2>
      <p>
        <b>13.1</b> Promotional credits grant Jiten+ access for a stated number of days. They are free, confer no purchase and no right to a refund, and may
        be withdrawn where obtained by abuse, including through multiple Accounts.
      </p>
      <p><b>13.2</b> Credit days count down only while no paid subscription is active.</p>
      <p>
        <b>13.3</b> The features included with a promotional credit are those stated at the time of redemption. Where a credit grants a restricted tier, the
        restriction is stated at redemption and in the confirming email.
      </p>
      <p>
        <b>13.4</b> Promotional credits do not renew and do not convert into a paid subscription automatically. No payment method is required to redeem one.
      </p>
      <p>
        <b>13.5 Gratuitous grants.</b> Jitensoft may grant Jiten+ access free of charge and at its discretion, whether for a fixed period or described as
        lifetime. A grant is gratuitous: no price is paid, it does not constitute a sale, and articles 4 to 9 and 12 do not apply to it. No payment method
        is held and nothing is ever charged.
      </p>
      <p>
        <b>13.6</b> A grant is personal to the Account, non-transferable, and confers no right to a refund, to compensation, or to a substitute benefit at
        any time, including where the Service ceases under article 8.4. Paragraphs a) and b) of article 8.4 nonetheless apply to grant holders in full.
      </p>
      <p>
        <b>13.7</b> A grant described as lifetime means the operational lifetime of the Service, on the same basis as articles 8.1 to 8.3. Jitensoft will
        honour such a grant for as long as the Service operates, and will not withdraw it in order to convert the holder into a paying subscriber. A grant
        may be withdrawn only where it was obtained by abuse, or where the Account is terminated for material breach under article 11 of the CGU.
      </p>
      <p>
        <b>13.8</b> Article 10 applies to grant holders in full. Nothing you uploaded or generated is deleted when a grant expires or is exhausted, and
        export of your data remains free.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">14. Suspension</h2>
      <p>
        We may suspend Jiten+ where payment fails as provided at article 6.3, where required by law, or on termination of the Account under CGU article 11.
        Suspension under CGU article 11 for material breach does not entitle you to a refund, except that where we terminate an Account holding a paid
        subscription for a reason not attributable to you, we will refund the unused portion of the current period pro rata, and a Lifetime holder may claim
        under article 8.4.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">15. Liability</h2>
      <p>
        Article 12 of the CGU applies to purchases under these CGV, save that the cap at CGU article 12.3 is, for a purchaser of Jiten+, not less than the
        total amount paid by that purchaser to Jitensoft.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">16. Personal data</h2>
      <p>
        Processing of personal data in connection with a purchase, including data shared with Stripe, is described in the
        <NuxtLink to="/privacy">Privacy Policy</NuxtLink>. The legal basis for processing billing data is performance of this contract and compliance with
        accounting obligations.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">17. Complaints, mediation and law</h2>
      <p><b>17.1</b> Address any complaint to <a href="mailto:contact@jiten.moe">contact@jiten.moe</a>.</p>
      <p>
        <b>17.2</b> If unresolved, a consumer may refer the dispute free of charge to the consumer mediator designated in the
        <NuxtLink to="/mentions-legales">Legal notice</NuxtLink>, within one year of the written complaint.
      </p>
      <p>
        <b>17.3</b> These CGV are governed by French law. Where you are a consumer resident in the European Union, this does not deprive you of the
        protection of mandatory provisions of the law of your country of residence, and you may bring proceedings in the courts of your place of residence.
      </p>
      <p>
        <b>17.4</b> The English version is operative. A French version is available at <NuxtLink to="/cgv-fr">jiten.moe/cgv-fr</NuxtLink>. For consumers
        resident in France, the French version prevails.
      </p>
    </section>
  </div>
</template>
