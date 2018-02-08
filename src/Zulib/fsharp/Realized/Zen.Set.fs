module Zen.Set

module Cost = Zen.Cost.Realized

type set<'a when 'a : comparison> =
    // A set, plus the number of elements in the set
    Set<'a> * uint32

type t<'a when 'a : comparison> = set<'a>

let nullSet : set<'a> = Set.empty, 0u

let contains (x:'a) (s:set<'a>) : Cost.t<bool, unit> =
    lazy ( Set.contains x (fst s) )
    |> Cost.C

let add (x:'a) ((s, n):set<'a>) : Cost.t<set<'a>, unit> =
    lazy (
        if s |> Set.contains x then s, n
        elif n <= System.UInt32.MaxValue - 1u then Set.add x s, n+1u
        else failwith "Exceeded max set size"
    )
    |> Cost.C

let remove (x:'a) ((s, n):set<'a>) : Cost.t<set<'a>, unit> =
    lazy (
        if not (s |> Set.contains x) then s, n
        else Set.remove x s, n-1u
    )
    |> Cost.C
