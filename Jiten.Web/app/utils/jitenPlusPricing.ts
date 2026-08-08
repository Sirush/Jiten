// Must match the Stripe Dashboard prices. The pricing cards and the CGV pages both
// render from here so the contract can never state a different price than checkout.
export const JITEN_PLUS_PRICES = {
  monthlyEur: 5,
  yearlyEur: 50,
  lifetimeEur: 150,
} as const;
