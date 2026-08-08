<script setup lang="ts">
  const { legal } = useLegalIdentity();

  useSeoMeta({ title: 'Privacy Policy' });
</script>

<template>
  <div class="legal-doc mx-auto max-w-3xl px-6 py-10 leading-relaxed text-gray-800 dark:text-gray-200">
    <h1 class="mb-2 text-center text-3xl font-bold text-gray-900 dark:text-gray-100">Privacy Policy for Jiten.moe</h1>
    <p class="mb-10 text-center italic text-gray-500 dark:text-gray-400">Last Updated: 8 August 2026</p>

    <p>
      Jiten.moe ("we", "our", "us") respects your privacy and is committed to protecting your personal data. This Privacy Policy explains how we collect,
      use, and protect your information when you use our website.
    </p>

    <section class="mb-10">
      <h2 class="section-title">1. Data Controller</h2>
      <p>
        The controller for personal data processed through Jiten is <b>Jitensoft</b>, entreprise individuelle, SIREN {{ legal.siren }},
        {{ legal.address }}, contact <a href="mailto:contact@jiten.moe">contact@jiten.moe</a>.
      </p>
      <p>Full identification of the controller is given in the <NuxtLink to="/mentions-legales">Legal notice</NuxtLink>.</p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">2. What Data We Collect</h2>
      <p>When you use Jiten.moe, we may collect:</p>
      <ul>
        <li>
          <b>Account Information:</b> Username (chosen by you) and email address. If you register or sign in with Google, we receive your email address
          from your Google account.
        </li>
        <li><b>Newsletter Preferences:</b> Whether you have opted in to receive our newsletter.</li>
        <li><b>Technical Data:</b> IP address, browser type, operating system (collected automatically for security and site functionality).</li>
        <li><b>Study Data:</b> Your vocabulary state, study history and preferences, processed to provide the learning features you use.</li>
        <li>
          <b>Audience Measurement:</b> Aggregate usage statistics collected by our self-hosted analytics (see section 10). IP addresses are anonymised and
          no advertising or cross-site tracking takes place.
        </li>
        <li><b>Payment and Subscription Data:</b> If you purchase Jiten+; see section 6.</li>
        <li>
          <b>Discord Link (optional):</b> If you choose to link your Discord account to Jiten, we store your Discord user identifier alongside your
          account in order to grant you server roles. You can unlink at any time, which deletes the stored identifier.
        </li>
      </ul>
    </section>

    <section class="mb-10">
      <h2 class="section-title">3. Legal Basis for Processing</h2>
      <p>We process your personal data on the following legal bases under GDPR:</p>
      <ul>
        <li><b>Contract (art. 6(1)(b)):</b> To provide access to your account and the services you request, and to manage Jiten+ purchases.</li>
        <li><b>Consent (art. 6(1)(a)):</b> If you opt in to receive newsletters.</li>
        <li>
          <b>Legitimate Interest (art. 6(1)(f)):</b> To maintain site security, prevent abuse and fraud, and measure aggregate audience with
          privacy-preserving analytics.
        </li>
        <li><b>Legal Obligation (art. 6(1)(c)):</b> To retain accounting and invoicing records.</li>
      </ul>
    </section>

    <section class="mb-10">
      <h2 class="section-title">4. How We Use Your Data</h2>
      <p>We use your data for:</p>
      <ul>
        <li>Providing and managing your account and study features.</li>
        <li>Sending newsletters (only if you opted in).</li>
        <li>Ensuring the security and performance of the website.</li>
        <li>Taking payment for and managing Jiten+ subscriptions.</li>
        <li>Complying with legal obligations.</li>
      </ul>
    </section>

    <section class="mb-10">
      <h2 class="section-title">5. Data Retention</h2>
      <ul>
        <li>Account data (username, email) is kept as long as your account is active.</li>
        <li>If you delete your account, we delete or anonymise your data within 30 days, unless retention is required by law.</li>
        <li>Newsletter data is kept until you unsubscribe.</li>
        <li>
          Accounting records relating to a purchase are retained for ten (10) years from the end of the financial year concerned, as required by article
          L123-22 of the French Commercial Code. This retention survives deletion of your account: after account deletion the retained records are reduced
          to what the accounting obligation requires and are no longer linked to a usable account.
        </li>
      </ul>
    </section>

    <section class="mb-10">
      <h2 class="section-title">6. Payment and Subscription Data</h2>
      <p>When you purchase Jiten+, the following additional data is processed.</p>
      <p>
        <b>What we hold.</b> Jitensoft stores your Stripe customer identifier, your subscription identifier, the plan purchased, the subscription status,
        the current period end date, whether cancellation is scheduled, the tier granted, promotional credits redeemed, and the record of the declaration
        you gave at checkout under article 9 of the <NuxtLink to="/cgv">Terms of Sale</NuxtLink>.
      </p>
      <p>
        <b>What we do not hold.</b> Jitensoft never receives or stores your card number, card expiry, security code or bank details. Payment credentials are
        collected directly by Stripe on its own pages.
      </p>
      <p>
        <b>Processor.</b> Payments are processed by Stripe Payments Europe, Ltd. (Ireland), acting as processor for transaction data and as an independent
        controller for its own fraud-prevention and regulatory purposes. Stripe's privacy notice:
        <a href="https://stripe.com/privacy" target="_blank" rel="noopener">https://stripe.com/privacy</a>. Stripe may transfer data outside the European
        Economic Area under the safeguards described in that notice.
      </p>
      <p>
        <b>Transactional email.</b> Email relating to a purchase, including confirmation, the renewal reminder required by article L215-1 of the French
        Consumer Code, payment failure notices and subscription-end notices, is sent on the basis of contract performance and is not marketing. It cannot be
        unsubscribed from while a subscription is active, though you may cancel the subscription.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">7. Files You Upload</h2>
      <p>
        Where you upload images or audio to your study cards using Jiten+, those files are stored on our content delivery network (Bunny.net, BunnyWay
        d.o.o., Slovenia) under access-controlled paths, and the associated metadata is stored in our database.
      </p>
      <p>
        These files are retained if your subscription ends. They are not deleted as a consequence of non-payment. You may view, download and delete them at
        any time, including on the free tier. See article 10 of the <NuxtLink to="/cgv">Terms of Sale</NuxtLink>.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">8. Sharing of Data</h2>
      <p>We do not sell or rent your personal data. We may share limited data with:</p>
      <ul>
        <li><b>Hosting</b> (Hetzner Online GmbH, Germany, for website operation).</li>
        <li><b>Content delivery</b> (Bunny.net, for serving media and files).</li>
        <li><b>Payment processing</b> (Stripe, as described in section 6).</li>
        <li><b>Email delivery</b> (Mailgun — Sinch group, EU region — for sending newsletters and transactional email).</li>
        <li><b>Sign-in and abuse prevention</b> (Google LLC: optional Google sign-in, and reCAPTCHA on registration, as described in section 10).</li>
        <li>
          <b>Discord</b> (Discord Inc., USA): when you report an issue on a media page, the comment you write is delivered to our private staff channel
          through a Discord webhook, together with your internal account identifier — a random ID that is not your username or email address. Our internal
          operational alerts are delivered the same way and reference at most those internal identifiers. Beyond the content of a report itself, your name,
          username and email address are never transmitted to Discord. If you choose to link your Discord account to Jiten (an optional feature), Discord
          also processes that link so we can grant you server roles. Discord may transfer data outside the EU/EEA under the safeguards described in its
          privacy policy: <a href="https://discord.com/privacy" target="_blank" rel="noopener">https://discord.com/privacy</a>.
        </li>
        <li>Authorities, if legally required.</li>
      </ul>
      <p>If data is transferred outside the EU/EEA, we ensure safeguards such as Standard Contractual Clauses (SCCs).</p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">9. Your Rights (under GDPR)</h2>
      <p>You have the right to:</p>
      <ul>
        <li>Access your personal data.</li>
        <li>Correct inaccuracies in your data.</li>
        <li>Request deletion of your data.</li>
        <li>Object to processing or request restriction.</li>
        <li>Withdraw consent at any time (for the newsletter).</li>
        <li>Request a copy of your data in a portable format.</li>
      </ul>
      <p>
        Access to your data, and export of it, is available free of charge from your account settings on the free tier, and is never conditional on holding
        a paid subscription.
      </p>
      <p>To exercise your rights, contact us at <a href="mailto:contact@jiten.moe">contact@jiten.moe</a>.</p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">10. Cookies and Analytics</h2>
      <ul>
        <li><b>Essential cookies</b> are required for website functionality, such as keeping you signed in and remembering your display preferences. They do
          not require consent.</li>
        <li>
          <b>Audience measurement.</b> We use a self-hosted instance of <a href="https://umami.is" target="_blank" rel="noopener">Umami</a>, a
          privacy-focused analytics tool, to understand aggregate site usage. It sets no cookies, stores no identifier on your device, anonymises IP
          addresses, and performs no advertising or cross-site tracking. The data stays on our own infrastructure and is not shared with anyone. Because no
          tracker is stored on your device and the data is used solely to produce anonymous aggregate statistics, this measurement operates under the
          audience-measurement exemption and no consent banner is required.
        </li>
        <li>
          <b>Abuse prevention.</b> The registration page uses Google reCAPTCHA to block automated sign-ups. reCAPTCHA is provided by Google LLC and
          processes technical and behavioural data, such as your IP address and interaction patterns, to distinguish humans from bots; this may involve
          transfers outside the EU/EEA under Google's safeguards. It loads only on the pages that use it. Google's privacy policy:
          <a href="https://policies.google.com/privacy" target="_blank" rel="noopener">https://policies.google.com/privacy</a>.
        </li>
        <li>
          <b>Google sign-in.</b> If you choose to sign in with Google, Google's sign-in component loads on the login and registration pages and Google
          processes that sign-in under its own privacy policy. Signing in with email and password involves no Google service.
        </li>
      </ul>
      <p>We use no advertising cookies and no third-party analytics services.</p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">11. Children's Privacy</h2>
      <p>
        Our services are not directed to individuals under <b>15 years old</b> (in accordance with French law). By registering, you confirm that you are at
        least 15 years old or have parental consent.
      </p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">12. Changes to This Privacy Policy</h2>
      <p>We may update this Privacy Policy from time to time. Changes will be posted on this page with an updated date.</p>
    </section>

    <section class="mb-10">
      <h2 class="section-title">13. Contact</h2>
      <p>For any privacy-related questions, contact: <a href="mailto:contact@jiten.moe">contact@jiten.moe</a></p>
    </section>
  </div>
</template>
