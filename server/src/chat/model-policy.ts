// Reviewed text + function-calling models. Intersect with the key's live list;
// listing a model does not prove inference access or sufficient quota.
export const recommendedGeminiModels = ["gemini-3.8-flash", "gemini-3.5-flash-lite"] as const;
export function isRecommendedGeminiModel(id: string): boolean {
  return (recommendedGeminiModels as readonly string[]).includes(id.replace(/^models\//, ""));
}
export function isRecommendedClaudeModel(model: { id?: string; lifecycle?: string }): boolean {
  return typeof model.id === "string" && /^claude-(sonnet|haiku|opus)-[45](?:[-.]|$)/.test(model.id)
    && !/preview|experimental|beta/.test(model.id) && (!model.lifecycle || model.lifecycle === "active");
}
export function claudeModelRank(id: string): number {
  return id.includes("sonnet") ? 0 : id.includes("haiku") ? 1 : 2;
}
