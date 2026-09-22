namespace BPlug

module BReusable =
    module String =
        let trim (s: string) =
            if isNull s then
                System.String.Empty
            else
                s.Trim()

        let join (parts: string seq) = String.Join(", ", parts)