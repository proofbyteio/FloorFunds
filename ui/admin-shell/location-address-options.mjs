// US / Canada address options for Create location and Create client shell forms.
// Country list is intentionally limited to US and CA. Region codes are ISO-3166-2
// subdivisions; time zones are IANA names the API stores as text.

export const COUNTRIES = [
  { code: "US", label: "United States (US)" },
  { code: "CA", label: "Canada (CA)" },
];

export const DEFAULT_COUNTRY = "US";

export const US_STATES = [
  ["AL", "Alabama"], ["AK", "Alaska"], ["AZ", "Arizona"], ["AR", "Arkansas"], ["CA", "California"],
  ["CO", "Colorado"], ["CT", "Connecticut"], ["DE", "Delaware"], ["DC", "District of Columbia"],
  ["FL", "Florida"], ["GA", "Georgia"], ["HI", "Hawaii"], ["ID", "Idaho"], ["IL", "Illinois"],
  ["IN", "Indiana"], ["IA", "Iowa"], ["KS", "Kansas"], ["KY", "Kentucky"], ["LA", "Louisiana"],
  ["ME", "Maine"], ["MD", "Maryland"], ["MA", "Massachusetts"], ["MI", "Michigan"], ["MN", "Minnesota"],
  ["MS", "Mississippi"], ["MO", "Missouri"], ["MT", "Montana"], ["NE", "Nebraska"], ["NV", "Nevada"],
  ["NH", "New Hampshire"], ["NJ", "New Jersey"], ["NM", "New Mexico"], ["NY", "New York"],
  ["NC", "North Carolina"], ["ND", "North Dakota"], ["OH", "Ohio"], ["OK", "Oklahoma"], ["OR", "Oregon"],
  ["PA", "Pennsylvania"], ["RI", "Rhode Island"], ["SC", "South Carolina"], ["SD", "South Dakota"],
  ["TN", "Tennessee"], ["TX", "Texas"], ["UT", "Utah"], ["VT", "Vermont"], ["VA", "Virginia"],
  ["WA", "Washington"], ["WV", "West Virginia"], ["WI", "Wisconsin"], ["WY", "Wyoming"],
].map(([code, label]) => ({ code, label: `${label} (${code})` }));

export const CA_PROVINCES = [
  ["AB", "Alberta"], ["BC", "British Columbia"], ["MB", "Manitoba"], ["NB", "New Brunswick"],
  ["NL", "Newfoundland and Labrador"], ["NS", "Nova Scotia"], ["NT", "Northwest Territories"],
  ["NU", "Nunavut"], ["ON", "Ontario"], ["PE", "Prince Edward Island"], ["QC", "Quebec"],
  ["SK", "Saskatchewan"], ["YT", "Yukon"],
].map(([code, label]) => ({ code, label: `${label} (${code})` }));

export const TIME_ZONES = [
  { value: "America/New_York", label: "Eastern Time (America/New_York)" },
  { value: "America/Chicago", label: "Central Time (America/Chicago)" },
  { value: "America/Denver", label: "Mountain Time (America/Denver)" },
  { value: "America/Phoenix", label: "Arizona (America/Phoenix)" },
  { value: "America/Los_Angeles", label: "Pacific Time (America/Los_Angeles)" },
  { value: "America/Anchorage", label: "Alaska (America/Anchorage)" },
  { value: "Pacific/Honolulu", label: "Hawaii (Pacific/Honolulu)" },
  { value: "America/Toronto", label: "Eastern Canada (America/Toronto)" },
  { value: "America/Winnipeg", label: "Central Canada (America/Winnipeg)" },
  { value: "America/Edmonton", label: "Mountain Canada (America/Edmonton)" },
  { value: "America/Vancouver", label: "Pacific Canada (America/Vancouver)" },
  { value: "America/Halifax", label: "Atlantic Canada (America/Halifax)" },
  { value: "America/St_Johns", label: "Newfoundland (America/St_Johns)" },
];

/** Field length limits for the create form (UI). Keep within common NA address practice. */
export const FIELD_LIMITS = {
  postal_code_us: 10, // 12345 or 12345-6789
  postal_code_ca: 7,  // A1A 1A1
  phone: 20,          // allows +1 and light formatting; E.164 digits ≤ 15
  email: 254,         // RFC 5321 practical upper bound
};

export function regionsFor(country) {
  if (country === "US") return US_STATES;
  if (country === "CA") return CA_PROVINCES;
  return [];
}

export function postalMaxLength(country) {
  if (country === "CA") return FIELD_LIMITS.postal_code_ca;
  return FIELD_LIMITS.postal_code_us;
}
