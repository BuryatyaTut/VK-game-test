"""Fetch current city weather and print statistics grouped by country."""

import argparse
import json
import math
import sys
from dataclasses import dataclass
from http.client import HTTPException
from pathlib import Path
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import quote
from urllib.request import Request, urlopen


@dataclass(frozen=True)
class WeatherData:
    city: str
    temperature_c: float
    country: str


class WeatherError(Exception):
    """A request failed or the response did not contain usable weather data."""


def load_cities(path: Path) -> list[str]:
    """Read unique, nonempty names; preserve the first spelling and order."""
    cities = []
    seen = set()
    # utf-8-sig also accepts ordinary UTF-8 and removes an optional BOM.
    for line in path.read_text(encoding="utf-8-sig").splitlines():
        city = line.strip()
        key = city.casefold()
        if city and key not in seen:
            cities.append(city)
            seen.add(key)
    return cities


def parse_weather(city: str, payload: Any) -> WeatherData:
    """Use the current temperature, not feels-like or forecast temperatures."""
    try:
        raw_temperature = payload["current_condition"][0]["temp_C"]
        country = payload["nearest_area"][0]["country"][0]["value"]
        if isinstance(raw_temperature, bool):
            raise ValueError("temperature is a boolean")
        temperature = float(raw_temperature)
        if not math.isfinite(temperature):
            raise ValueError("temperature is not finite")
        if not isinstance(country, str) or not country.strip():
            raise ValueError("country is empty or not a string")
    except (KeyError, IndexError, TypeError, ValueError, OverflowError) as exc:
        raise WeatherError("missing or invalid current temperature/country") from exc
    return WeatherData(city, temperature, country.strip())


def fetch_weather(city: str, timeout: float = 15.0) -> WeatherData:
    """Make one HTTPS request for a city, with a socket timeout."""
    url = f"https://wttr.in/{quote(city, safe='')}?format=j1"
    request = Request(url, headers={"Accept": "application/json"})
    try:
        with urlopen(request, timeout=timeout) as response:
            payload = json.load(response)
    except HTTPError as exc:
        raise WeatherError(f"HTTP {exc.code}: {exc.reason}") from exc
    except URLError as exc:
        raise WeatherError(f"request failed: {exc.reason}") from exc
    except (OSError, HTTPException) as exc:
        raise WeatherError(f"connection failed: {exc}") from exc
    except ValueError as exc:
        raise WeatherError("response is not valid JSON") from exc
    return parse_weather(city, payload)


def format_temperature(value: float) -> str:
    """Show at most one decimal place, an explicit sign, and Celsius units."""
    rounded = round(value, 1)
    if rounded == 0:
        rounded = 0.0  # Avoid displaying negative zero after rounding.
    return f"{rounded:+.1f}".rstrip("0").rstrip(".") + " °C"


def print_report(weather: list[WeatherData], requested_count: int) -> None:
    if len(weather) != requested_count:
        print(
            f"PARTIAL REPORT: {len(weather)}/{requested_count} cities available; "
            "statistics include successful requests only.\n"
        )
    print("Current weather:")
    countries: dict[str, list[float]] = {}
    for item in weather:
        print(f"{item.city}, {item.country} {format_temperature(item.temperature_c)}")
        countries.setdefault(item.country, []).append(item.temperature_c)

    print("\nBy country:")
    for country, temperatures in sorted(countries.items()):
        count = len(temperatures)
        average = sum(temperatures) / count
        city_label = "city" if count == 1 else "cities"
        print(
            f"{country} — {count} {city_label}, "
            f"avg: {format_temperature(average)}, "
            f"min: {format_temperature(min(temperatures))}, "
            f"max: {format_temperature(max(temperatures))}"
        )


def positive_timeout(value: str) -> float:
    try:
        timeout = float(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("timeout must be a positive number") from exc
    if not math.isfinite(timeout) or timeout <= 0:
        raise argparse.ArgumentTypeError("timeout must be a finite positive number")
    return timeout


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "cities_file", nargs="?", type=Path,
        default=Path(__file__).resolve().with_name("cities.txt"),
        help="UTF-8 file with one city per line (default: cities.txt beside this script)",
    )
    parser.add_argument(
        "--timeout", type=positive_timeout, default=15.0,
        help="socket timeout per request in seconds (default: 15)",
    )
    args = parser.parse_args(argv)
    try:
        cities = load_cities(args.cities_file)
    except (OSError, UnicodeError) as exc:
        print(f"Error reading cities file: {exc}", file=sys.stderr)
        return 1
    if not cities:
        print("Error: the cities file contains no city names.", file=sys.stderr)
        return 1

    weather = []
    for city in cities:
        try:
            weather.append(fetch_weather(city, args.timeout))
        except WeatherError as exc:
            print(f"Error for {city}: {exc}", file=sys.stderr)

    if not weather:
        print("No weather data retrieved; no statistics available.", file=sys.stderr)
        return 1
    print_report(weather, len(cities))
    return 0 if len(weather) == len(cities) else 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        print("\nInterrupted.", file=sys.stderr)
        sys.exit(130)
