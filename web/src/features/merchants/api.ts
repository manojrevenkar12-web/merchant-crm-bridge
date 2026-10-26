import { keepPreviousData, useQuery } from "@tanstack/react-query";

import { api } from "../../shared/api/client";
import type { MerchantSummary } from "../../shared/api/types";
import { limits, refresh } from "../../shared/theme/tokens";

export function useMerchants(search: string, stage: string) {
  const params = new URLSearchParams({ top: String(limits.merchantsPage) });
  if (search) params.set("search", search);
  if (stage) params.set("stage", stage);
  return useQuery({
    queryKey: ["merchants", search, stage],
    queryFn: () => api.get<MerchantSummary[]>(`/v1/merchants?${params}`),
    refetchInterval: refresh.list,
    placeholderData: keepPreviousData,
  });
}
