import { useQuery } from "@tanstack/react-query";

import { api } from "../../shared/api/client";
import type { MerchantDetail } from "../../shared/api/types";
import { refresh } from "../../shared/theme/tokens";

export const useMerchant = (id: string) =>
  useQuery({
    queryKey: ["merchant", id],
    queryFn: () => api.get<MerchantDetail>(`/v1/merchants/${encodeURIComponent(id)}`),
    refetchInterval: refresh.detail,
  });
