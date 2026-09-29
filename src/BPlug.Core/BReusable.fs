namespace BPlug

module BReusable =
    module String =
        let trim (s: string) =
            if isNull s then
                System.String.Empty
            else
                s.Trim()

        let toLowerInvariant (s: string) =
            if isNull s then
                System.String.Empty
            else
                s.ToLowerInvariant()

        let join (parts: string seq) = System.String.Join(", ", parts)