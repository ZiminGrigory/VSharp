namespace VSharp

open VSharp.Terms

module internal Merging =

    let private boolMerge = function
        | [] -> []
        | [_] as gvs -> gvs
        | [(g1, v1); (g2, v2)] -> [(g1 ||| g2, (g1 &&& v1) ||| (g2 &&& v2))]
        | (g, v)::gvs ->
            let guard = List.fold (|||) g (List.map fst gvs) in
            let value = List.fold (fun acc (g, v) -> acc ||| (g &&& v)) (g &&& v) gvs in
            [(guard, value)]

    let private typedMerge gvs t =
        match t with
        | Bool -> boolMerge gvs
        | Numeric _
        | String
        | _ -> gvs

    let private simplify gvs =
        let rec loop gvs out =
            match gvs with
            | [] -> out
            | (Terms.True, v)::gvs' -> [List.head gvs]
            | (Terms.False, v)::gvs' -> loop gvs' out
            | (g, Union us)::gvs' when not (List.isEmpty us) ->
                loop gvs' (List.append (Unions.guardWith g us) out)
            | gv::gvs' -> loop gvs' (gv::out)
        loop gvs []

    let internal mergeSame = function
        | [] -> []
        | [_] as xs -> xs
        | [(g1, v1); (g2, v2)] as gvs -> if v1 = v2 then [(g1 ||| g2, v1)] else gvs
        | gvs ->
            let rec loop gvs out =
                match gvs with
                | [] -> out
                | (g, v)::gvs' ->
                    let eq, rest = List.partition (snd >> (=) v) gvs' in
                    let joined = List.fold (|||) g (List.map fst eq)
                    if Terms.IsTrue joined then [(joined, v)]
                    else loop rest ((joined, v)::out)
            loop gvs []

    let private compress = function
        | [] -> []
        | [_] as gvs -> gvs
        | [(_, v1); (_, v2)] as gvs when TypeOf v1 = TypeOf v2 -> typedMerge (mergeSame gvs) (TypeOf v1)
        | [_; _] as gvs -> gvs
        | gvs -> List.groupBy (snd >> TypeOf) gvs |> List.map (fun (t, gvs) -> typedMerge gvs t) |> List.concat

    let internal merge gvs state =
        match compress (simplify gvs) with
        | [(g, v)] -> (v, State.addAssertion state g)
        | gvs' -> (Union gvs', state)

    let internal merge2Terms g h u v =
        match g, h, u, v with
        | _, _, _, _ when u = v -> u
        | True, _, _, _ -> u
        | False, _, _, _ -> v
        | _, True, _, _ -> v
        | _, False, _, _ -> u
        | _ -> merge [(g, u); (h, v)] State.empty |> fst

    let internal merge2States condition1 condition2 state1 state2 =
        match condition1, condition2 with
        | True, _ -> state1
        | False, _ -> state2
        | _, True -> state2
        | _, False -> state1
        | _ ->
            assert(State.path state1 = State.path state2)
            assert(State.frames state1 = State.frames state2)
            let mergeIfShould id u =
                if State.hasEntry state2 id
                then merge2Terms condition1 condition2 u (State.eval state2 id)
                else u
            state1
                |> State.mapKeys mergeIfShould
                |> State.union state2
                |> State.withAssertions (State.uniteAssertions (State.assertions state1) (State.assertions state2))

    let internal mergeStates conditions states =
        let gcs = List.zip conditions states in
        let merger (g1, s1) (g2, s2) = (g1 ||| g2, merge2States g1 g2 s1 s2) in
        match gcs with
        | [] -> State.empty
        | [(_, s)] -> s
        | _ -> List.reduce merger gcs |> snd
