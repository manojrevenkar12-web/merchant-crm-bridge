import { useQuery } from "@tanstack/react-query";

import { api } from "../../shared/api/client";
import type { Overview } from "../../shared/api/types";
import { refresh } from "../../shared/theme/tokens";

export const useOverview = () =>
  useQuery({
    queryKey: ["overview"],
    queryFn: () => api.get<Overview>("/v1/overview"),
    refetchInterval: refresh.overview,
  });
